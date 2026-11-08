namespace DiscImageStudio.App.Avalonia.Design;

/// <summary>
/// Runs an action once, <see cref="Delay"/> after the last <see cref="Schedule"/>. The wait uses
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, so the action resumes on the caller's
/// synchronization context (the UI thread) and behaves the same under the headless platform.
/// </summary>
public sealed class Debouncer(TimeSpan delay, Action action)
{
    private CancellationTokenSource? _pending;

    public TimeSpan Delay { get; } = delay;

    public bool IsPending => _pending is not null;

    public void Schedule()
    {
        _pending?.Cancel();
        CancellationTokenSource pending = new();
        _pending = pending;
        _ = RunAsync(pending);
    }

    /// <summary>Drops a scheduled run without executing it.</summary>
    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
    }

    private async Task RunAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(Delay, pending.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_pending, pending))
        {
            _pending = null;
            action();
        }
    }
}
