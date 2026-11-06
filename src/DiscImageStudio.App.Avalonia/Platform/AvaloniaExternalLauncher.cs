using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class AvaloniaExternalLauncher(Func<TopLevel?> topLevel) : IExternalLauncher
{
    public async Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        bool launched = await RequireLauncher().LaunchUriAsync(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return launched;
    }

    public async Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        ILauncher launcher = RequireLauncher();
        bool launched = Directory.Exists(fullPath)
            ? await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(fullPath))
            : await launcher.LaunchFileInfoAsync(new FileInfo(fullPath));
        cancellationToken.ThrowIfCancellationRequested();
        return launched;
    }

    private ILauncher RequireLauncher()
        => topLevel()?.Launcher
           ?? throw new InvalidOperationException("A visible top-level window is required to launch external content.");
}
