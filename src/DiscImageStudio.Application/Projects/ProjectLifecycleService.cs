using DiscImageStudio.Application.Navigation;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Results;
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

    public ProjectLifecycleService(
        IProjectDocumentStore store,
        IFileSystem fileSystem,
        IClock clock,
        IUserInteraction interaction,
        IApplicationEventPublisher? events = null,
        NavigationState? navigation = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _events = events ?? new NullApplicationEventPublisher();
        Navigation = navigation ?? new NavigationState();
    }

    public ProjectWorkspace? Current { get; private set; }

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

        try
        {
            _ = ProjectDocumentMapper.ToDocument(snapshot.Workspace);
            Current = snapshot.Workspace.DeepCopy();
            MarkDirty(reasonCode, origin);
            return ApplicationResult.Success();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.InvalidRequest, exception.Message, exception);
        }
    }

    public ApplicationResult ReplaceWorkspace(
        ProjectWorkspace workspace,
        string reasonCode,
        string origin)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Current is null)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.NoProjectOpen);
        }

        try
        {
            _ = ProjectDocumentMapper.ToDocument(workspace);
            Current = workspace.DeepCopy();
            MarkDirty(reasonCode, origin);
            return ApplicationResult.Success();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return ApplicationResult.Failure(ApplicationErrorCode.InvalidRequest, exception.Message, exception);
        }
    }

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
            ProjectIdentity identity = new(
                Current.Identity.Name,
                Current.Identity.CreatedUtc,
                _clock.UtcNow,
                Current.Identity.CalibrationSessionPath,
                Current.Identity.ThumbnailPngBase64);
            ProjectWorkspace saved = Current with { Identity = identity };
            string fullPath = _fileSystem.GetFullPath(path);
            _store.Save(fullPath, ProjectDocumentMapper.ToDocument(saved));
            Current = saved;
            ProjectFilePath = fullPath;
            Dirty.AcceptChanges();
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
        Dirty.AcceptChanges();
        Navigation.ProjectOpened();
        _events.Publish(new ProjectOpened(fullPath, workspace.Identity.Name));
    }
}
