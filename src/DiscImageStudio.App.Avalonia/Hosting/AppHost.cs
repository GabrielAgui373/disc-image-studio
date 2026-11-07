using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.App.Avalonia.ViewModels;
using DiscImageStudio.App.Avalonia.Views;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.App.Avalonia.Hosting;

public sealed class AppHost : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly UnhandledExceptionMonitor _exceptionMonitor;
    private readonly AvaloniaApplicationLifetime _applicationLifetime;
    private bool _retryingDesktopShutdown;

    private AppHost(
        IClassicDesktopStyleApplicationLifetime desktop,
        UnhandledExceptionMonitor exceptionMonitor,
        AvaloniaApplicationLifetime applicationLifetime,
        MainWindow mainWindow,
        HostServices services)
    {
        _desktop = desktop;
        _exceptionMonitor = exceptionMonitor;
        _applicationLifetime = applicationLifetime;
        MainWindow = mainWindow;
        Services = services;
        _desktop.ShutdownRequested += OnShutdownRequested;
    }

    public MainWindow MainWindow { get; }

    public HostServices Services { get; }

    public static AppHost Create(App app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(desktop);

        XdgAppPaths paths = XdgAppPaths.CreateDefault();
        TryCreateDirectories(paths);
        RotatingFileLogger logger = new(paths.LogsDirectory);
        UnhandledExceptionMonitor exceptionMonitor = new(logger);
        PhysicalAtomicFileStorage storage = new();
        JsonSettingsStore settingsStore = new(paths.SettingsFilePath, storage, logger);
        UserSettings settings = settingsStore.Load();
        StartupPreferences.Apply(app, settings);

        EnglishTextCatalog text = new();
        MainWindow? mainWindow = null;
        Func<TopLevel?> topLevel = () => mainWindow;
        Func<Window?> owner = () => mainWindow;
        AvaloniaFilePickerService filePicker = new(topLevel);
        AvaloniaClipboardService clipboard = new(topLevel);
        AvaloniaExternalLauncher launcher = new(topLevel);
        AvaloniaUiDispatcher dispatcher = new();
        OverlayUserPromptService prompts = new(text);
        JsonRecentProjectStore recentProjects = new(paths.RecentProjectsFilePath, storage, logger);
        FileCacheManager cache = new(paths, logger);
        ApplicationEventHub applicationEvents = new();
        JobCoordinator jobs = new(new SystemClock(), applicationEvents);
        PhysicalFileSystem fileSystem = new();
        ProjectLifecycleService projects = new(
            new JsonProjectDocumentStore(),
            fileSystem,
            new SystemClock(),
            prompts,
            applicationEvents);
        AvaloniaApplicationLifetime applicationLifetime = new(
            jobs,
            prompts,
            settingsStore,
            settings,
            text,
            logger);
        AboutInfo about = AboutInfo.Create(paths);

        HostServices services = new(
            paths,
            filePicker,
            clipboard,
            launcher,
            dispatcher,
            prompts,
            settingsStore,
            recentProjects,
            cache,
            applicationLifetime,
            logger,
            jobs,
            projects,
            settings,
            text,
            about);

        MainViewModel viewModel = new(
            paths,
            filePicker,
            fileSystem,
            clipboard,
            launcher,
            text,
            about,
            projects,
            jobs,
            dispatcher,
            applicationEvents,
            settings);
        prompts.Attach(viewModel);
        mainWindow = new MainWindow
        {
            DataContext = viewModel,
        };
        mainWindow.AttachLifetime(applicationLifetime);

        logger.Log(AppLogLevel.Information, "host.started", "Avalonia host services were composed successfully.");
        return new AppHost(desktop, exceptionMonitor, applicationLifetime, mainWindow, services);
    }

    public void Dispose()
    {
        _desktop.ShutdownRequested -= OnShutdownRequested;
        _exceptionMonitor.Dispose();
        _applicationLifetime.Dispose();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        if (_applicationLifetime.IsShutdownReady || _retryingDesktopShutdown)
        {
            return;
        }

        args.Cancel = true;
        if (await _applicationLifetime.RequestShutdownAsync())
        {
            _retryingDesktopShutdown = true;
            _desktop.TryShutdown();
        }
    }

    private static void TryCreateDirectories(XdgAppPaths paths)
    {
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Disc Image Studio could not create every XDG directory: {exception.Message}");
        }
    }
}

public sealed record HostServices(
    IAppPaths Paths,
    IFilePickerService FilePicker,
    IClipboardService Clipboard,
    IExternalLauncher Launcher,
    IUiDispatcher Dispatcher,
    IUserPromptService Prompts,
    ISettingsStore SettingsStore,
    IRecentProjectStore RecentProjects,
    ICacheManager Cache,
    DiscImageStudio.Application.Ports.IApplicationLifetime ApplicationLifetime,
    IAppLogger Logger,
    JobCoordinator Jobs,
    ProjectLifecycleService Projects,
    UserSettings Settings,
    ITextCatalog Text,
    AboutInfo About);
