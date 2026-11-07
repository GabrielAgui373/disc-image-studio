using DiscImageStudio.Application.Projects;

namespace DiscImageStudio.Application.Editing;

/// <summary>
/// One undoable step: the workspace before and after it, a label code for menus ("Undo
/// Rename layer") and the layers to reselect after undo or redo.
/// </summary>
public sealed record HistoryEntry(
    string Label,
    ProjectWorkspace Before,
    ProjectWorkspace After,
    IReadOnlyList<string> AffectedLayerIds)
{
    /// <summary>
    /// Approximate retained size. Snapshots share unchanged layers, so this deliberately counts
    /// only per-snapshot structure and text, not shared image data (there is none: layers hold
    /// paths, and decoded images live in the composition cache).
    /// </summary>
    public long EstimatedBytes { get; } = Estimate(Before) + Estimate(After);

    private static long Estimate(ProjectWorkspace workspace)
        => 512 + workspace.Design.Layers.Sum(layer => 160L + ((layer.Id.Length + layer.Name.Length + layer.ImagePath.Length) * 2L));
}

/// <summary>
/// Linear undo/redo stack of workspace snapshots, bounded by entry count and estimated bytes.
/// Recording a new step discards the redo branch; the oldest steps are dropped first.
/// </summary>
public sealed class ProjectHistory
{
    public const int DefaultMaximumEntries = 200;
    public const long DefaultMaximumBytes = 8L * 1024 * 1024;

    private readonly List<HistoryEntry> _entries = [];
    private int _position;

    public ProjectHistory(int maximumEntries = DefaultMaximumEntries, long maximumBytes = DefaultMaximumBytes)
    {
        MaximumEntries = maximumEntries > 0 ? maximumEntries : throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        MaximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    }

    public int MaximumEntries { get; }

    public long MaximumBytes { get; }

    public int Count => _entries.Count;

    public bool CanUndo => _position > 0;

    public bool CanRedo => _position < _entries.Count;

    public HistoryEntry? NextUndo => CanUndo ? _entries[_position - 1] : null;

    public HistoryEntry? NextRedo => CanRedo ? _entries[_position] : null;

    public long EstimatedBytes => _entries.Sum(entry => entry.EstimatedBytes);

    public void Record(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.RemoveRange(_position, _entries.Count - _position);
        _entries.Add(entry);
        _position = _entries.Count;
        Trim();
    }

    /// <summary>Moves one step back and returns the step to revert, or null at the start.</summary>
    public HistoryEntry? StepBack()
    {
        if (!CanUndo)
        {
            return null;
        }

        _position--;
        return _entries[_position];
    }

    /// <summary>Moves one step forward and returns the step to reapply, or null at the end.</summary>
    public HistoryEntry? StepForward()
    {
        if (!CanRedo)
        {
            return null;
        }

        HistoryEntry entry = _entries[_position];
        _position++;
        return entry;
    }

    public void Clear()
    {
        _entries.Clear();
        _position = 0;
    }

    private void Trim()
    {
        long bytes = EstimatedBytes;
        while (_entries.Count > 1 && (_entries.Count > MaximumEntries || bytes > MaximumBytes))
        {
            bytes -= _entries[0].EstimatedBytes;
            _entries.RemoveAt(0);
            _position--;
        }
    }
}
