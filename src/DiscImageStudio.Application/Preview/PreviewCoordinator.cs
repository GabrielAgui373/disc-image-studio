using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Preview;

/// <summary>
/// Routes preview requests to the service registered for their family and mode, and keeps one
/// calculation per channel: a newer request cancels the older one, whose result is dropped.
/// Hosts therefore never show a picture of a revision older than the one they last asked for.
/// </summary>
public sealed class PreviewCoordinator
{
    private readonly Dictionary<(OpticalDiscFamily, PreviewMode), IPreviewService> _services;
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public PreviewCoordinator(IEnumerable<IPreviewService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = [];
        foreach (IPreviewService service in services)
        {
            if (!_services.TryAdd((service.Family, service.Mode), service))
            {
                throw new ArgumentException(
                    $"Two preview services were registered for {service.Family} {service.Mode}.",
                    nameof(services));
            }
        }
    }

    public bool Supports(OpticalDiscFamily family, PreviewMode mode) => _services.ContainsKey((family, mode));

    /// <summary>
    /// Renders <paramref name="request"/> on <paramref name="channel"/>. Returns
    /// <see langword="null"/> when a newer request on the same channel (or
    /// <see cref="Cancel"/>) superseded it; failures surface as
    /// <see cref="PreviewFailedException"/>.
    /// </summary>
    public async Task<PreviewResult?> RenderLatestAsync(
        string channel,
        PreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(request);
        if (!_services.TryGetValue((request.Family, request.Mode), out IPreviewService? service))
        {
            throw new PreviewFailedException(
                PreviewFailureCode.Unsupported,
                $"No preview service renders {request.Family} {request.Mode}.");
        }

        Running running = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        lock (_lock)
        {
            if (_running.TryGetValue(channel, out Running? previous))
            {
                previous.Cancellation.Cancel();
            }

            _running[channel] = running;
        }

        try
        {
            PreviewResult result = await service.RenderAsync(request, running.Cancellation.Token).ConfigureAwait(false);
            return IsCurrent(channel, running) ? result : null;
        }
        catch (Exception) when (running.Cancellation.IsCancellationRequested)
        {
            // Superseded or canceled: whatever the service threw on the way out is moot.
            return null;
        }
        finally
        {
            lock (_lock)
            {
                if (_running.TryGetValue(channel, out Running? current) && ReferenceEquals(current, running))
                {
                    _running.Remove(channel);
                }
            }

            running.Cancellation.Dispose();
        }
    }

    /// <summary>Cancels the calculation running on <paramref name="channel"/>, if any.</summary>
    public void Cancel(string channel)
    {
        lock (_lock)
        {
            if (_running.Remove(channel, out Running? running))
            {
                running.Cancellation.Cancel();
            }
        }
    }

    /// <summary>Cancels every running calculation, for example when the project closes.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            foreach (Running running in _running.Values)
            {
                running.Cancellation.Cancel();
            }

            _running.Clear();
        }
    }

    private bool IsCurrent(string channel, Running running)
    {
        lock (_lock)
        {
            return _running.TryGetValue(channel, out Running? current) && ReferenceEquals(current, running)
                   && !running.Cancellation.IsCancellationRequested;
        }
    }

    private sealed class Running(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
    }
}
