using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.App.Avalonia.ViewModels;
using DiscImageStudio.App.Avalonia.Views;
using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

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
        JsonProjectDocumentStore projectDocuments = new();
        // CD composes against the scheme's fixed image radius; the CD module owns that value.
        // One decoded-layer cache serves composition (preview, thumbnail, generation) and the
        // design canvas, so a layer image is decoded once per file version.
        CachingLayerImageSource layerImages = new();
        ProjectCompositionService compositions = new(DiscImageStudio.Cd.CdDiscParameters.StandardImageOuterRadiusMm, layerImages);
        ProjectLifecycleService projects = new(
            projectDocuments,
            fileSystem,
            new SystemClock(),
            prompts,
            applicationEvents,
            recentProjects: recentProjects,
            thumbnails: compositions);
        RecentProjectsService recentProjectsService = new(recentProjects, projectDocuments, fileSystem);
        AvaloniaApplicationLifetime applicationLifetime = new(
            jobs,
            projects,
            prompts,
            settingsStore,
            settings,
            text,
            logger);
        AboutInfo about = AboutInfo.Create(paths);

        // Projection sources are the 8192 px composite generation reads, cached per design
        // content in the XDG cache folder and shared by every preview of that content.
        DiscImageStudio.Application.Preview.PreviewSourceCache previewSources = new(
            compositions,
            Path.Combine(paths.CacheDirectory, "preview"));

        // One typed pipeline for planning (Export review) and running exports.
        DiscImageStudio.Generation.DiscGenerationService generation = new();

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
            about,
            // Generation sources are disposable composites; they live in the XDG cache folder.
            new DiscImageStudio.App.Avalonia.Generation.ProjectGenerationPreparer(
                compositions,
                Path.Combine(paths.CacheDirectory, "generation")),
            previewSources,
            new DiscImageStudio.Application.Preview.PreviewCoordinator(
                DiscImageStudio.Preview.PreviewServices.CreateAll(previewSources)));

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
            settings,
            recentProjectsService,
            new AvaloniaThumbnailDecoder(),
            new ProjectAssetService(fileSystem, new PhysicalDirectoryScanner()),
            new UserPresetCatalog(Path.Combine(paths.ConfigDirectory, DiscPresetJsonStore.FileName), logger),
            compositions,
            new DiscImageStudio.Application.Editing.ProjectEditor(projects),
            services.Previews,
            new DiscImageStudio.App.Avalonia.Generation.ExportPlanner(services.PreviewSources, generation),
            new DiscImageStudio.App.Avalonia.Generation.CalibrationTargetRunner(generation));
        viewModel.ExportWorkspace.AttachRunner(
            new DiscImageStudio.App.Avalonia.Generation.ExportRunner(services.Generation, generation),
            jobs,
            launcher,
            clipboard);
        viewModel.CalibrationWorkspace.AttachRunner(jobs, launcher, clipboard);
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
        Services.Previews.CancelAll();
        Services.PreviewSources.Dispose();
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
    AboutInfo About,
    DiscImageStudio.App.Avalonia.Generation.ProjectGenerationPreparer Generation,
    DiscImageStudio.Application.Preview.PreviewSourceCache PreviewSources,
    DiscImageStudio.Application.Preview.PreviewCoordinator Previews);
