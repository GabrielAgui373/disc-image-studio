using Avalonia.Threading;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Default, cancellationToken);
    }
}
