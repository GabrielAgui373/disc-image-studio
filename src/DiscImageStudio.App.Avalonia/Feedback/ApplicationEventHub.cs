using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Feedback;

public interface IApplicationEventStream
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;
}

/// <summary>
/// Process-local bridge for typed application events. Application services only depend on
/// <see cref="IApplicationEventPublisher"/>; the Avalonia shell chooses how to observe them.
/// </summary>
public sealed class ApplicationEventHub : IApplicationEventPublisher, IApplicationEventStream
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public void Publish<TEvent>(TEvent applicationEvent) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        Delegate[] subscribers;
        lock (_gate)
        {
            subscribers = _handlers.TryGetValue(typeof(TEvent), out List<Delegate>? handlers)
                ? [.. handlers]
                : [];
        }

        foreach (Action<TEvent> subscriber in subscribers.Cast<Action<TEvent>>())
        {
            subscriber(applicationEvent);
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out List<Delegate>? handlers))
            {
                handlers = [];
                _handlers.Add(typeof(TEvent), handlers);
            }

            handlers.Add(handler);
        }

        return new Subscription(() => Unsubscribe(handler));
    }

    private void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out List<Delegate>? handlers))
            {
                return;
            }

            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                _handlers.Remove(typeof(TEvent));
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
