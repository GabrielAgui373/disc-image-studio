using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Navigation;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Projects;

public sealed record CreateProjectRequest(
    string FilePath,
    string Name,
    MediaState Media,
    DeliveryState Delivery,
    DesignState? Design = null,
    ToneState? Tone = null,
    string? CalibrationSessionPath = null,
    string? ThumbnailPngBase64 = null);

public sealed record ProjectOpened(string FilePath, string ProjectName);

public sealed record ProjectSaved(string FilePath, string ProjectName);

public sealed record ProjectClosed(string ProjectName);

public sealed record ProjectDirtyChanged(bool IsDirty, DirtyReason? Reason);

/// <summary>
/// The open workspace content changed. <see cref="IsPreview"/> marks transient states (a drag
/// in progress) that are neither recorded in history nor evaluated for dirty state.
/// </summary>
public sealed record ProjectWorkspaceChanged(Guid SessionId, long ContentRevision, bool IsPreview);

/// <summary>
/// Owns the current project session. Calls are serialized by the presentation host; this
/// service performs blocking JSON I/O on the calling thread and publishes events synchronously.
/// </summary>
public sealed class ProjectLifecycleService
{
    private readonly IProjectDocumentStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IClock _clock;
    private readonly IUserInteraction _interaction;
    private readonly IApplicationEventPublisher _events;
    private readonly IRecentProjectStore? _recentProjects;
    private readonly IProjectThumbnailRenderer? _thumbnails;

    public ProjectLifecycleService(
        IProjectDocumentStore store,
        IFileSystem fileSystem,
        IClock clock,
        IUserInteraction interaction,
        IApplicationEventPublisher? events = null,
        NavigationState? navigation = null,
        IRecentProjectStore? recentProjects = null,
        IProjectThumbnailRenderer? thumbnails = null)
    {
        _recentProjects = recentProjects;
        _thumbnails = thumbnails;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _events = events ?? new NullApplicationEventPublisher();
        Navigation = navigation ?? new NavigationState();
    }

    public ProjectWorkspace? Current { get; private set; }

    /// <summary>Changes whenever a project is created, opened or closed; editors reset history on change.</summary>
    public Guid SessionId { get; private set; }

    /// <summary>
    /// Incremented for every committed content change (open, edit, undo). Saving does not change
    /// it, and previews do not either, so an editor can detect changes made around it.
    /// </summary>
    public long ContentRevision { get; private set; }

    private string? _savedFingerprint;

    public string? ProjectFilePath { get; private set; }

    public bool HasOpenProject => Current is not null;

    public DirtyState Dirty { get; } = new();

    public NavigationState Navigation { get; }

    public async Task<ApplicationResult<ProjectWorkspace>> CreateProjectAsync(
        CreateProjectRequest request,
        string replacementContext = "create-project",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplicationResult replace = await EnsureCanReplaceProjectAsync(replacementContext, cancellationToken);
        if (!replace.Succeeded)
        {
            return ApplicationResult<ProjectWorkspace>.Failure(
                replace.Error!.Code,
                replace.Error.Detail,
                replace.Error.Exception);
        }

        try
        {
            DateTimeOffset now = _clock.UtcNow;
            ProjectWorkspace workspace = new(
                new ProjectIdentity(
                    request.Name,
                    now,
                    now,
                    request.CalibrationSessionPath,
                    request.ThumbnailPngBase64),
                request.Design ?? new DesignState(),
                request.Tone ?? new ToneState(ToneProfile.Photo),
                request.Delivery,
                request.Media);
            string fullPath = _fileSystem.GetFullPath(request.FilePath);
            _store.Save(fullPath, ProjectDocumentMapper.ToDocument(workspace));
            OpenWorkspace(fullPath, workspace);
            return ApplicationResult<ProjectWorkspace>.Success(workspace);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<ProjectWorkspace>.Failure(
                exception is ArgumentException ? ApplicationErrorCode.InvalidRequest : ApplicationErrorCode.ProjectWriteFailed,
                exception.Message,
                exception);
        }
    }

    public async Task<ApplicationResult<ProjectWorkspace>> OpenProjectAsync(
        string path,
        string replacementContext = "open-project",
        CancellationToken cancellationToken = default)
    {
        ApplicationResult replace = await EnsureCanReplaceProjectAsync(replacementContext, cancellationToken);
        if (!replace.Succeeded)
        {
            return ApplicationResult<ProjectWorkspace>.Failure(
                replace.Error!.Code,
                replace.Error.Detail,
                replace.Error.Exception);
        }

        try
        {
            string fullPath = _fileSystem.GetFullPath(path);
            ProjectWorkspace workspace = ProjectDocumentMapper.ToWorkspace(_store.Load(fullPath));
            OpenWorkspace(fullPath, workspace);
            return ApplicationResult<ProjectWorkspace>.Success(workspace);
        }
        catch (InvalidDataException exception) when (ProjectJsonStore.IsUnsupportedSchema(exception))
        {
            return ApplicationResult<ProjectWorkspace>.Failure(
                ApplicationErrorCode.ProjectSchemaUnsupported,
                exception.Message,
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<ProjectWorkspace>.Failure(
                ApplicationErrorCode.ProjectReadFailed,
                exception.Message,
                exception);
        }
    }

    public ApplicationResult<ProjectSnapshot> CaptureProjectSnapshot()
        => Current is null
            ? ApplicationResult<ProjectSnapshot>.Failure(ApplicationErrorCode.NoProjectOpen)
            : ApplicationResult<ProjectSnapshot>.Success(new ProjectSnapshot(Current.DeepCopy()));

    public ApplicationResult ApplyProjectSnapshot(
        ProjectSnapshot snapshot,
        string reasonCode = "snapshot-applied",
        string origin = "application")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Current is null)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.NoProjectOpen);
        }

        return ApplyEdit(snapshot.Workspace, reasonCode, origin);
    }

    public ApplicationResult ReplaceWorkspace(
        ProjectWorkspace workspace,
        string reasonCode,
        string origin)
        => ApplyEdit(workspace, reasonCode, origin);

    /// <summary>
    /// Commits an edited workspace. The persisted identity fields owned by saving (creation and
    /// modification time, thumbnail) are kept from the current project, so undoing an edit never
    /// rolls back a save. The project is dirty exactly when its content differs from the last
    /// saved or opened state: returning to that state (for example by undo) makes it clean again.
    /// </summary>
    public ApplicationResult ApplyEdit(ProjectWorkspace workspace, string reasonCode, string origin)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (Current is null)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.NoProjectOpen);
        }

        ProjectWorkspace committed;
        string fingerprint;
        try
        {
            committed = KeepSaveOwnedIdentity(workspace, Current);
            fingerprint = ContentFingerprint(committed);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.InvalidRequest, exception.Message, exception);
        }

        Current = committed;
        ContentRevision++;
        if (string.Equals(fingerprint, _savedFingerprint, StringComparison.Ordinal))
        {
            if (Dirty.IsDirty)
            {
                Dirty.AcceptChanges();
                _events.Publish(new ProjectDirtyChanged(false, null));
            }
        }
        else
        {
            MarkDirty(reasonCode, origin);
        }

        _events.Publish(new ProjectWorkspaceChanged(SessionId, ContentRevision, IsPreview: false));
        return ApplicationResult.Success();
    }

    /// <summary>
    /// Shows a transient workspace, such as an in-progress drag, without touching dirty state or
    /// <see cref="ContentRevision"/>. The editor must finish with <see cref="ApplyEdit"/> or
    /// restore the original state.
    /// </summary>
    public ApplicationResult PreviewEdit(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Current is null)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.NoProjectOpen);
        }

        Current = KeepSaveOwnedIdentity(workspace, Current);
        _events.Publish(new ProjectWorkspaceChanged(SessionId, ContentRevision, IsPreview: true));
        return ApplicationResult.Success();
    }

    /// <summary>
    /// Identity of the persisted content, ignoring the fields that only saving changes
    /// (modification time and thumbnail). Equal fingerprints mean nothing needs saving.
    /// </summary>
    public static string ContentFingerprint(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ProjectDocument document = ProjectDocumentMapper.ToDocument(workspace) with
        {
            ModifiedUtc = default,
            ThumbnailPngBase64 = null,
        };
        return System.Text.Json.JsonSerializer.Serialize(document);
    }

    private static ProjectWorkspace KeepSaveOwnedIdentity(ProjectWorkspace edited, ProjectWorkspace current)
        => edited with
        {
            Identity = new ProjectIdentity(
                edited.Identity.Name,
                current.Identity.CreatedUtc,
                current.Identity.ModifiedUtc,
                edited.Identity.CalibrationSessionPath,
                current.Identity.ThumbnailPngBase64),
        };

    public bool MarkDirty(
        string reasonCode,
        string origin,
        ChangePersistence persistence = ChangePersistence.Persistent)
    {
        if (Current is null)
        {
            return false;
        }

        bool changed = Dirty.MarkDirty(reasonCode, origin, _clock.UtcNow, persistence);
        if (changed)
        {
            _events.Publish(new ProjectDirtyChanged(true, Dirty.Reasons[^1]));
        }

        return changed;
    }

    public Task<ApplicationResult<ProjectWorkspace>> SaveProjectAsync(CancellationToken cancellationToken = default)
        => ProjectFilePath is null
            ? Task.FromResult(ApplicationResult<ProjectWorkspace>.Failure(ApplicationErrorCode.NoProjectOpen))
            : SaveProjectAsAsync(ProjectFilePath, cancellationToken);

    public Task<ApplicationResult<ProjectWorkspace>> SaveProjectAsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Current is null)
        {
            return Task.FromResult(ApplicationResult<ProjectWorkspace>.Failure(ApplicationErrorCode.NoProjectOpen));
        }

        try
        {
            string fullPath = _fileSystem.GetFullPath(path);
            ProjectIdentity identity = new(
                Current.Identity.Name,
                Current.Identity.CreatedUtc,
                _clock.UtcNow,
                Current.Identity.CalibrationSessionPath,
                RenderThumbnail(Current, fullPath, cancellationToken) ?? Current.Identity.ThumbnailPngBase64);
            ProjectWorkspace saved = Current with { Identity = identity };
            _store.Save(fullPath, ProjectDocumentMapper.ToDocument(saved));
            Current = saved;
            ProjectFilePath = fullPath;
            _savedFingerprint = ContentFingerprint(saved);
            Dirty.AcceptChanges();
            RecordRecent(fullPath, saved);
            _events.Publish(new ProjectDirtyChanged(false, null));
            _events.Publish(new ProjectSaved(fullPath, saved.Identity.Name));
            return Task.FromResult(ApplicationResult<ProjectWorkspace>.Success(saved));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ApplicationResult<ProjectWorkspace>.Failure(
                ApplicationErrorCode.ProjectWriteFailed,
                exception.Message,
                exception));
        }
    }

    public async Task<ApplicationResult> CloseProjectAsync(
        string context = "close-project",
        CancellationToken cancellationToken = default)
    {
        if (Current is null)
        {
            return ApplicationResult.Success();
        }

        ApplicationResult replace = await EnsureCanReplaceProjectAsync(context, cancellationToken);
        if (!replace.Succeeded)
        {
            return replace;
        }

        string name = Current.Identity.Name;
        Current = null;
        ProjectFilePath = null;
        _savedFingerprint = null;
        SessionId = Guid.Empty;
        ContentRevision++;
        Dirty.AcceptChanges();
        Navigation.ProjectClosed();
        _events.Publish(new ProjectClosed(name));
        return ApplicationResult.Success();
    }

    private async Task<ApplicationResult> EnsureCanReplaceProjectAsync(
        string context,
        CancellationToken cancellationToken)
    {
        if (Current is null || !Dirty.IsDirty)
        {
            return ApplicationResult.Success();
        }

        UnsavedChangesDecision decision = await _interaction.ConfirmUnsavedChangesAsync(
            new UnsavedChangesRequest(Current.Identity.Name, context),
            cancellationToken);
        if (decision == UnsavedChangesDecision.Cancel)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.UserCanceled);
        }

        if (decision == UnsavedChangesDecision.Save)
        {
            ApplicationResult<ProjectWorkspace> saved = await SaveProjectAsync(cancellationToken);
            return saved.Succeeded
                ? ApplicationResult.Success()
                : ApplicationResult.Failure(
                    saved.Error!.Code,
                    saved.Error.Detail,
                    saved.Error.Exception);
        }

        return ApplicationResult.Success();
    }

    private void OpenWorkspace(string fullPath, ProjectWorkspace workspace)
    {
        Current = workspace.DeepCopy();
        ProjectFilePath = fullPath;
        SessionId = Guid.NewGuid();
        ContentRevision++;
        _savedFingerprint = ContentFingerprint(Current);
        Dirty.AcceptChanges();
        Navigation.ProjectOpened();
        RecordRecent(fullPath, workspace);
        _events.Publish(new ProjectOpened(fullPath, workspace.Identity.Name));
    }

    /// <summary>
    /// Renders the thumbnail written with the project. A design with nothing to paint, or any
    /// rendering failure, keeps the previous thumbnail: a thumbnail never blocks a save.
    /// </summary>
    private string? RenderThumbnail(ProjectWorkspace workspace, string fullPath, CancellationToken cancellationToken)
    {
        if (_thumbnails is null || workspace.Design.Layers.Count == 0)
        {
            return null;
        }

        try
        {
            return _thumbnails.RenderThumbnail(workspace, fullPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the project in the history before the open/save event is published, so
    /// presentation layers refreshing on that event already see it. The store degrades to a
    /// logged warning on I/O failure; history never blocks a project operation.
    /// </summary>
    private void RecordRecent(string fullPath, ProjectWorkspace workspace)
        => _recentProjects?.Record(new RecentProjectEntry(
            workspace.Identity.Name,
            RecentProjectsService.ToFamilyToken(workspace.Media.Family),
            fullPath,
            _clock.UtcNow,
            workspace.Identity.ThumbnailPngBase64));
}
