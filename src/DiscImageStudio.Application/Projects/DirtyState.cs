namespace DiscImageStudio.Application.Projects;

public enum ChangePersistence
{
    Persistent,
    Ephemeral,
}

public sealed record DirtyReason(string Code, string Origin, DateTimeOffset OccurredUtc);

/// <summary>Tracks only changes that alter the persisted project document.</summary>
public sealed class DirtyState
{
    private readonly List<DirtyReason> _reasons = [];

    public bool IsDirty => _reasons.Count > 0;

    public long Revision { get; private set; }

    public IReadOnlyList<DirtyReason> Reasons => _reasons.AsReadOnly();

    public bool MarkDirty(
        string reasonCode,
        string origin,
        DateTimeOffset occurredUtc,
        ChangePersistence persistence = ChangePersistence.Persistent)
    {
        if (persistence == ChangePersistence.Ephemeral)
        {
            return false;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        _reasons.Add(new DirtyReason(reasonCode, origin, occurredUtc));
        Revision++;
        return true;
    }

    public void AcceptChanges() => _reasons.Clear();
}
