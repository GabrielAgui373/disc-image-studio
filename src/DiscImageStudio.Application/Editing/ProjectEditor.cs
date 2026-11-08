using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;

namespace DiscImageStudio.Application.Editing;

/// <summary>Outcome of an edit, undo or redo: the label and the layers to select afterwards.</summary>
public sealed record EditOutcome(string Label, IReadOnlyList<string> AffectedLayerIds);

/// <summary>
/// The single path for undoable project edits. Every persistent change goes through
/// <see cref="Apply"/> or a <see cref="DragEdit"/>, is recorded as one history step and is
/// committed with <see cref="ProjectLifecycleService.ApplyEdit"/>, which keeps dirty state equal
/// to "content differs from the last save". View state (zoom, pan, selection, hover) never comes
/// here. History resets when the project session changes, and is dropped if the workspace is
/// changed outside the editor, so undo can never resurrect a state the user did not see.
/// </summary>
public sealed class ProjectEditor
{
    private const string Origin = "design-editor";

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectHistory _history;
    private Guid _sessionId;
    private long _knownRevision = -1;
    private DragEdit? _activeDrag;

    public ProjectEditor(ProjectLifecycleService projects, ProjectHistory? history = null)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _history = history ?? new ProjectHistory();
    }

    /// <summary>Raised after any change to what can be undone or redone.</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>True when a project is open and no drag is in progress, so a new edit can start.</summary>
    public bool CanApply => Synchronize() && _activeDrag is null;

    public bool CanUndo => Synchronize() && _history.CanUndo && _activeDrag is null;

    public bool CanRedo => Synchronize() && _history.CanRedo && _activeDrag is null;

    public string? UndoLabel => CanUndo ? _history.NextUndo?.Label : null;

    public string? RedoLabel => CanRedo ? _history.NextRedo?.Label : null;

    /// <summary>
    /// Applies <paramref name="edit"/> to the current workspace as one undoable step.
    /// Use a <see cref="DesignEdits"/> function, and pass the layers the UI should select.
    /// </summary>
    public ApplicationResult<EditOutcome> Apply(
        string label,
        Func<ProjectWorkspace, ProjectWorkspace> edit,
        IReadOnlyList<string>? affectedLayerIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(edit);
        if (_activeDrag is not null)
        {
            return ApplicationResult<EditOutcome>.Failure(ApplicationErrorCode.InvalidRequest, "A drag is in progress.");
        }

        Synchronize();
        ProjectWorkspace? before = _projects.Current;
        if (before is null)
        {
            return ApplicationResult<EditOutcome>.Failure(ApplicationErrorCode.NoProjectOpen);
        }

        ProjectWorkspace after;
        try
        {
            after = edit(before);
        }
        catch (ArgumentException exception)
        {
            return ApplicationResult<EditOutcome>.Failure(ApplicationErrorCode.InvalidRequest, exception.Message, exception);
        }

        return Commit(label, before, after, affectedLayerIds ?? []);
    }

    /// <summary>
    /// Starts a continuous edit (dragging a layer, a slider). Updates are previews; only
    /// <see cref="DragEdit.Commit"/> records a single history step. Disposing an uncommitted
    /// drag cancels it and restores the starting state.
    /// </summary>
    public DragEdit BeginDrag(string label, IReadOnlyList<string>? affectedLayerIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (_activeDrag is not null)
        {
            throw new InvalidOperationException("Only one drag can be active.");
        }

        Synchronize();
        ProjectWorkspace start = _projects.Current
            ?? throw new InvalidOperationException("No project is open.");
        _activeDrag = new DragEdit(this, label, start, affectedLayerIds ?? []);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return _activeDrag;
    }

    public ApplicationResult<EditOutcome> Undo()
    {
        if (!CanUndo)
        {
            return ApplicationResult<EditOutcome>.Failure(ApplicationErrorCode.InvalidRequest, "Nothing to undo.");
        }

        HistoryEntry entry = _history.StepBack()!;
        return Restore(entry, entry.Before, "undo");
    }

    public ApplicationResult<EditOutcome> Redo()
    {
        if (!CanRedo)
        {
            return ApplicationResult<EditOutcome>.Failure(ApplicationErrorCode.InvalidRequest, "Nothing to redo.");
        }

        HistoryEntry entry = _history.StepForward()!;
        return Restore(entry, entry.After, "redo");
    }

    private ApplicationResult<EditOutcome> Restore(HistoryEntry entry, ProjectWorkspace target, string direction)
    {
        ApplicationResult applied = _projects.ApplyEdit(target, $"{direction}:{entry.Label}", Origin);
        if (!applied.Succeeded)
        {
            // The stored state is no longer valid (should not happen); drop history rather than
            // leave the stack pointing at a state the project does not have.
            _history.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return ApplicationResult<EditOutcome>.Failure(applied.Error!.Code, applied.Error.Detail, applied.Error.Exception);
        }

        _knownRevision = _projects.ContentRevision;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return ApplicationResult<EditOutcome>.Success(new EditOutcome(entry.Label, entry.AffectedLayerIds));
    }

    private ApplicationResult<EditOutcome> Commit(
        string label,
        ProjectWorkspace before,
        ProjectWorkspace after,
        IReadOnlyList<string> affectedLayerIds)
    {
        bool unchanged = string.Equals(
            ProjectLifecycleService.ContentFingerprint(before),
            ProjectLifecycleService.ContentFingerprint(after),
            StringComparison.Ordinal);
        if (unchanged)
        {
            // Nothing persistent changed (for example a drag released where it started).
            if (!ReferenceEquals(_projects.Current, before))
            {
                _projects.PreviewEdit(before);
            }

            return ApplicationResult<EditOutcome>.Success(new EditOutcome(label, affectedLayerIds));
        }

        ApplicationResult applied = _projects.ApplyEdit(after, label, Origin);
        if (!applied.Succeeded)
        {
            return ApplicationResult<EditOutcome>.Failure(applied.Error!.Code, applied.Error.Detail, applied.Error.Exception);
        }

        // Record what was actually committed (identity fields kept by the lifecycle).
        _history.Record(new HistoryEntry(label, before, _projects.Current!, affectedLayerIds));
        _knownRevision = _projects.ContentRevision;
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return ApplicationResult<EditOutcome>.Success(new EditOutcome(label, affectedLayerIds));
    }

    /// <summary>
    /// Resets history when the session changed (open, create, close) or when the workspace was
    /// committed by someone else, because history steps assume they own every change.
    /// </summary>
    private bool Synchronize()
    {
        bool sessionChanged = _sessionId != _projects.SessionId;
        bool changedElsewhere = _knownRevision >= 0 && _knownRevision != _projects.ContentRevision && _activeDrag is null;
        if (sessionChanged || changedElsewhere)
        {
            _history.Clear();
            _sessionId = _projects.SessionId;
            _knownRevision = _projects.ContentRevision;
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_knownRevision < 0)
        {
            _knownRevision = _projects.ContentRevision;
        }

        return _projects.HasOpenProject;
    }

    /// <summary>A continuous edit that becomes at most one history step.</summary>
    public sealed class DragEdit : IDisposable
    {
        private readonly ProjectEditor _editor;
        private readonly string _label;
        private readonly IReadOnlyList<string> _affected;
        private ProjectWorkspace _latest;
        private bool _finished;

        internal DragEdit(ProjectEditor editor, string label, ProjectWorkspace start, IReadOnlyList<string> affected)
        {
            _editor = editor;
            _label = label;
            Start = start;
            _latest = start;
            _affected = affected;
        }

        /// <summary>The workspace when the drag began; updates are computed from it, not accumulated.</summary>
        public ProjectWorkspace Start { get; }

        /// <summary>Shows a preview computed from <see cref="Start"/>. Invalid intermediate values are ignored.</summary>
        public bool Update(Func<ProjectWorkspace, ProjectWorkspace> edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            ObjectDisposedException.ThrowIf(_finished, this);
            try
            {
                ProjectWorkspace next = edit(Start);
                if (_editor._projects.PreviewEdit(next).Succeeded)
                {
                    _latest = next;
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // A transient invalid value (for example a zero scale mid-drag) keeps the last preview.
            }

            return false;
        }

        public ApplicationResult<EditOutcome> Commit()
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            _finished = true;
            _editor._activeDrag = null;
            return _editor.Commit(_label, Start, _latest, _affected);
        }

        /// <summary>Restores the starting state without recording anything.</summary>
        public void Cancel()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _editor._activeDrag = null;
            _editor._projects.PreviewEdit(Start);
            _editor.HistoryChanged?.Invoke(_editor, EventArgs.Empty);
        }

        public void Dispose() => Cancel();
    }
}
