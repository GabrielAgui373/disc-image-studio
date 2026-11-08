using Avalonia.Controls;
using Avalonia.Input.Platform;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class AvaloniaClipboardService(Func<TopLevel?> topLevel) : IClipboardService
{
    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = await RequireClipboard().TryGetTextAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return value;
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireClipboard().SetTextAsync(text);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IClipboard RequireClipboard()
        => topLevel()?.Clipboard
           ?? throw new InvalidOperationException("A visible top-level window is required to use the clipboard.");
}
