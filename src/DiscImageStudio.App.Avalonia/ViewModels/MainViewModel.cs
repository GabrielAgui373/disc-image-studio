using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IAppPaths _paths;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLauncher _launcher;
    private readonly ITextCatalog _text;
    private readonly AboutInfo _about;

    public MainViewModel(
        IAppPaths paths,
        IClipboardService clipboard,
        IExternalLauncher launcher,
        ITextCatalog text,
        AboutInfo about)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _about = about ?? throw new ArgumentNullException(nameof(about));

        Title = text["app.title"];
        StatusTitle = text["host.status.title"];
        StatusDescription = text["host.status.description"];
        AboutTitle = text["about.title"];
        VersionLabel = text["about.version"];
        CommitLabel = text["about.commit"];
        RuntimeLabel = text["about.runtime"];
        AvaloniaLabel = text["about.avalonia"];
        OperatingSystemLabel = text["about.operatingSystem"];
        ConfigDirectoryLabel = text["about.configDirectory"];
        DataDirectoryLabel = text["about.dataDirectory"];
        CacheDirectoryLabel = text["about.cacheDirectory"];
        LogsDirectoryLabel = text["about.logsDirectory"];
        CopyDiagnosticsLabel = text["action.copyDiagnostics"];
        OpenLogsLabel = text["action.openLogs"];
    }

    public string Title { get; }
    public string StatusTitle { get; }
    public string StatusDescription { get; }
    public string AboutTitle { get; }
    public string VersionLabel { get; }
    public string CommitLabel { get; }
    public string RuntimeLabel { get; }
    public string AvaloniaLabel { get; }
    public string OperatingSystemLabel { get; }
    public string ConfigDirectoryLabel { get; }
    public string DataDirectoryLabel { get; }
    public string CacheDirectoryLabel { get; }
    public string LogsDirectoryLabel { get; }
    public string CopyDiagnosticsLabel { get; }
    public string OpenLogsLabel { get; }
    public string ProductVersion => _about.ProductVersion;
    public string Commit => _about.Commit;
    public string DotnetRuntime => _about.DotnetRuntime;
    public string AvaloniaVersion => _about.AvaloniaVersion;
    public string OperatingSystem => _about.OperatingSystem;
    public string ConfigDirectory => _about.ConfigDirectory;
    public string DataDirectory => _about.DataDirectory;
    public string CacheDirectory => _about.CacheDirectory;
    public string LogsDirectory => _about.LogsDirectory;

    [ObservableProperty]
    public partial string? ActionStatus { get; private set; }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        await _clipboard.SetTextAsync(BuildDiagnostics());
        ActionStatus = _text["action.diagnosticsCopied"];
    }

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        bool opened = await _launcher.OpenPathAsync(_paths.LogsDirectory);
        ActionStatus = opened ? _text["action.logsOpened"] : _text["action.logsOpenFailed"];
    }

    private string BuildDiagnostics()
        => $"{Title}\nVersion: {ProductVersion}\nCommit: {Commit}\n.NET: {DotnetRuntime}\n"
           + $"Avalonia: {AvaloniaVersion}\nOS: {OperatingSystem}\nConfig: {ConfigDirectory}\n"
           + $"Data: {DataDirectory}\nCache: {CacheDirectory}\nLogs: {LogsDirectory}";
}
