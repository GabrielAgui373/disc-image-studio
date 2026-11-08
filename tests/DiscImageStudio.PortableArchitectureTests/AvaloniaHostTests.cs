using System.Globalization;
using Avalonia.Styling;
using DiscImageStudio.App.Avalonia;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Hosting;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.App.Avalonia.ViewModels;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class AvaloniaHostTests
{
    [Fact]
    public void Xdg_paths_honor_every_override_and_create_private_directories()
    {
        string root = CreateTempDirectory();
        Dictionary<string, string> environment = new()
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        XdgAppPaths paths = new(
            key => environment.GetValueOrDefault(key),
            Path.Combine(root, "home"),
            Path.Combine(root, "documents"));

        paths.EnsureCreated();

        Assert.Equal(Path.Combine(root, "config", "disc-image-studio"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "data", "disc-image-studio"), paths.DataDirectory);
        Assert.Equal(Path.Combine(root, "cache", "disc-image-studio"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(root, "state", "disc-image-studio"), paths.StateDirectory);
        Assert.Equal(Path.Combine(paths.StateDirectory, "logs"), paths.LogsDirectory);
        Assert.All(
            new[] { paths.ConfigDirectory, paths.DataDirectory, paths.CacheDirectory, paths.StateDirectory, paths.LogsDirectory },
            directory => Assert.True(Directory.Exists(directory)));
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(paths.ConfigDirectory);
            Assert.Equal(
                (UnixFileMode)0,
                mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
    }

    [Fact]
    public void Invalid_settings_recover_to_English_defaults()
    {
        string path = Path.Combine(CreateTempDirectory(), "settings.json");
        File.WriteAllText(path, "{ invalid json");
        RecordingLogger logger = new();

        UserSettings settings = new JsonSettingsStore(path, logger: logger).Load();

        Assert.Equal("en", settings.Culture);
        Assert.Equal(ApplicationTheme.System, settings.Theme);
        Assert.Contains(logger.Events, entry => entry.EventName == "settings.load.recovered");
    }

    [Theory]
    [InlineData("read only")]
    [InlineData("disk full")]
    [InlineData("interrupted write")]
    public void Settings_write_failures_propagate_without_replacing_the_previous_value(string failure)
    {
        MemoryAtomicStorage storage = new(new UserSettings { Culture = "en", Theme = ApplicationTheme.Light });
        JsonSettingsStore store = new("/virtual/settings.json", storage);
        storage.WriteFailure = new IOException(failure);

        IOException exception = Assert.Throws<IOException>(
            () => store.Save(new UserSettings { Culture = "pt-BR", Theme = ApplicationTheme.Dark }));

        Assert.Equal(failure, exception.Message);
        Assert.Equal(ApplicationTheme.Light, store.Load().Theme);
        Assert.Equal("en", store.Load().Culture);
    }

    [Fact]
    public async Task Shutdown_blocks_new_jobs_cancels_active_work_and_persists_settings()
    {
        JobCoordinator jobs = new(new FixedClock());
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApplicationResult<JobCompletion>> running = jobs.StartJobAsync(
            new JobRequest("generation"),
            async context =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            });
        await started.Task;
        FakePrompts prompts = new(confirmShutdown: true);
        MemorySettingsStore settingsStore = new();
        AvaloniaApplicationLifetime lifetime = new(
            jobs,
            CreateLifecycle(prompts),
            prompts,
            settingsStore,
            new UserSettings { Culture = "en", Theme = ApplicationTheme.Dark },
            new EnglishTextCatalog(),
            new RecordingLogger());

        Assert.True(await lifetime.RequestShutdownAsync());
        ApplicationResult<JobCompletion> canceled = await running;
        ApplicationResult<JobCompletion> rejected = await jobs.StartJobAsync(
            new JobRequest("late"),
            _ => Task.CompletedTask);

        Assert.Equal(ApplicationErrorCode.JobCanceled, canceled.Error?.Code);
        Assert.Equal(ApplicationErrorCode.ApplicationShuttingDown, rejected.Error?.Code);
        Assert.True(lifetime.IsShutdownReady);
        Assert.True(lifetime.ShutdownToken.IsCancellationRequested);
        Assert.Equal(ApplicationTheme.Dark, settingsStore.Saved?.Theme);
    }

    [Fact]
    public async Task Shutdown_without_jobs_does_not_prompt()
    {
        FakePrompts prompts = new(confirmShutdown: false);
        AvaloniaApplicationLifetime lifetime = new(
            new JobCoordinator(new FixedClock()),
            CreateLifecycle(prompts),
            prompts,
            new MemorySettingsStore(),
            new UserSettings(),
            new EnglishTextCatalog(),
            new RecordingLogger());

        Assert.True(await lifetime.RequestShutdownAsync());
        Assert.Equal(0, prompts.ShutdownPromptCount);
    }

    [Fact]
    public async Task Main_view_model_uses_injected_platform_services_without_Avalonia_application_state()
    {
        FakePaths paths = new(CreateTempDirectory());
        FakeClipboard clipboard = new();
        FakeLauncher launcher = new();
        AboutInfo about = new(
            "1.0.0",
            "abc123",
            ".NET test",
            "12.1.4",
            "Linux test",
            paths.ConfigDirectory,
            paths.DataDirectory,
            paths.CacheDirectory,
            paths.LogsDirectory);
        MainViewModel viewModel = CreateViewModel(paths, clipboard, launcher, about, CreateLifecycle(new FakePrompts(confirmShutdown: true)));

        await viewModel.CopyDiagnosticsCommand.ExecuteAsync(null);
        await viewModel.OpenLogsCommand.ExecuteAsync(null);

        Assert.Contains("abc123", clipboard.Text, StringComparison.Ordinal);
        Assert.Equal(paths.LogsDirectory, launcher.LastPath);
        Assert.Equal("Logs folder opened.", viewModel.ActionStatus);
    }

    [Fact]
    public async Task Shutdown_with_unsaved_project_keeps_session_when_prompt_is_canceled()
    {
        FakePrompts prompts = new(confirmShutdown: true, unsavedDecision: UnsavedChangesDecision.Cancel);
        ProjectLifecycleService projects = CreateLifecycle(prompts);
        string path = Path.Combine(CreateTempDirectory(), "shutdown.discproj");
        Assert.True((await projects.CreateProjectAsync(CreateProjectRequest(path))).Succeeded);
        projects.MarkDirty("layer-moved", "test");
        AvaloniaApplicationLifetime lifetime = new(
            new JobCoordinator(new FixedClock()),
            projects,
            prompts,
            new MemorySettingsStore(),
            new UserSettings(),
            new EnglishTextCatalog(),
            new RecordingLogger());

        Assert.False(await lifetime.RequestShutdownAsync());

        Assert.False(lifetime.IsShutdownStarted);
        Assert.True(projects.HasOpenProject);
        Assert.True(projects.Dirty.IsDirty);
        Assert.Equal(1, prompts.UnsavedPromptCount);
    }

    [Fact]
    public async Task Project_shortcuts_are_ignored_while_the_new_project_dialog_is_open()
    {
        FakePaths paths = new(CreateTempDirectory());
        ProjectLifecycleService projects = CreateLifecycle(new FakePrompts(confirmShutdown: true));
        MainViewModel viewModel = CreateViewModel(paths, new FakeClipboard(), new FakeLauncher(), CreateAbout(paths), projects);
        string path = Path.Combine(paths.ProjectsDirectory, "shortcuts.discproj");
        Assert.True((await projects.CreateProjectAsync(CreateProjectRequest(path))).Succeeded);

        viewModel.StartNewProjectCommand.Execute(null);
        await viewModel.CloseProjectCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsAnyDialogOpen);
        Assert.True(projects.HasOpenProject);
    }

    [Fact]
    public void Startup_preferences_apply_English_culture_and_theme_before_a_window_exists()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            global::DiscImageStudio.App.Avalonia.App app = new();
            StartupPreferences.Apply(app, new UserSettings
            {
                Culture = "en",
                Theme = ApplicationTheme.Dark,
            });

            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

            StartupPreferences.Apply(app, new UserSettings
            {
                Culture = "en",
                Theme = ApplicationTheme.Light,
            });

            Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

            StartupPreferences.Apply(app, new UserSettings
            {
                Culture = "en",
                Theme = ApplicationTheme.System,
            });

            Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Missing_display_returns_actionable_Linux_diagnostic()
    {
        string? error = DisplayEnvironment.GetStartupError(_ => null, isLinux: true);

        Assert.Contains("X11 or XWayland", error, StringComparison.Ordinal);
        Assert.Null(DisplayEnvironment.GetStartupError(key => key == "DISPLAY" ? ":0" : null, isLinux: true));
    }

    private static ProjectLifecycleService CreateLifecycle(IUserInteraction interaction)
        => new(
            new JsonProjectDocumentStore(),
            new PhysicalFileSystem(),
            new FixedClock(),
            interaction);

    private static CreateProjectRequest CreateProjectRequest(string path)
        => new(
            path,
            "Host project",
            new CdState(24.5, 56.8, 359_849, writeCue: true, interleave: true),
            new DeliveryState("output.wav", string.Empty, "CD_IMAGE"));

    private static AboutInfo CreateAbout(FakePaths paths)
        => new(
            "1.0.0",
            "abc123",
            ".NET test",
            "12.1.4",
            "Linux test",
            paths.ConfigDirectory,
            paths.DataDirectory,
            paths.CacheDirectory,
            paths.LogsDirectory);

    /// <summary>Single construction point so host-composition changes touch one place.</summary>
    private static MainViewModel CreateViewModel(
        FakePaths paths,
        IClipboardService clipboard,
        IExternalLauncher launcher,
        AboutInfo about,
        ProjectLifecycleService projects)
        => new(
            paths,
            new FakeFilePicker(),
            new PhysicalFileSystem(),
            clipboard,
            launcher,
            new EnglishTextCatalog(),
            about,
            projects,
            new JobCoordinator(new FixedClock()),
            new InlineDispatcher(),
            new ApplicationEventHub(),
            new UserSettings(),
            new RecentProjectsService(
                new JsonRecentProjectStore(paths.RecentProjectsFilePath),
                new JsonProjectDocumentStore(),
                new PhysicalFileSystem()),
            new AvaloniaThumbnailDecoder(),
            new ProjectAssetService(new PhysicalFileSystem(), new PhysicalDirectoryScanner()),
            new UserPresetCatalog(Path.Combine(paths.ConfigDirectory, DiscPresetJsonStore.FileName)),
            new DiscImageStudio.Application.Composition.ProjectCompositionService(57.5),
            new DiscImageStudio.Application.Editing.ProjectEditor(projects),
            new DiscImageStudio.Application.Preview.PreviewCoordinator([]),
            new DiscImageStudio.App.Avalonia.Generation.ExportPlanner(
                new DiscImageStudio.Application.Preview.PreviewSourceCache(
                    new DiscImageStudio.Application.Composition.ProjectCompositionService(57.5),
                    Path.Combine(paths.CacheDirectory, "preview")),
                new DiscImageStudio.Generation.DiscGenerationService()));

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"disc-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<(AppLogLevel Level, string EventName)> Events { get; } = [];

        public void Log(
            AppLogLevel level,
            string eventName,
            string message,
            Exception? exception = null,
            Guid? correlationId = null)
            => Events.Add((level, eventName));
    }

    private sealed class MemoryAtomicStorage : IAtomicFileStorage
    {
        private byte[] _content;

        public MemoryAtomicStorage(UserSettings initial)
        {
            using MemoryStream stream = new();
            System.Text.Json.JsonSerializer.Serialize(stream, initial, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
            });
            _content = stream.ToArray();
        }

        public IOException? WriteFailure { get; set; }

        public bool Exists(string path) => true;

        public Stream OpenRead(string path) => new MemoryStream(_content, writable: false);

        public void WriteAtomically(string path, Action<Stream> write)
        {
            using MemoryStream candidate = new();
            write(candidate);
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            _content = candidate.ToArray();
        }
    }

    private sealed class FakePrompts(
        bool confirmShutdown,
        UnsavedChangesDecision unsavedDecision = UnsavedChangesDecision.Cancel) : IUserPromptService
    {
        public int ShutdownPromptCount { get; private set; }

        public int UnsavedPromptCount { get; private set; }

        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
            UnsavedChangesRequest request,
            CancellationToken cancellationToken = default)
        {
            UnsavedPromptCount++;
            return Task.FromResult(unsavedDecision);
        }

        public Task<bool> ConfirmShutdownAsync(int activeJobCount, CancellationToken cancellationToken = default)
        {
            ShutdownPromptCount++;
            return Task.FromResult(confirmShutdown);
        }

        public Task ShowErrorAsync(
            string title,
            string message,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public UserSettings? Saved { get; private set; }

        public UserSettings Load() => Saved ?? new UserSettings();

        public void Save(UserSettings settings) => Saved = settings;
    }

    private sealed class FakePaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");
        public string DataDirectory { get; } = Path.Combine(root, "data");
        public string CacheDirectory { get; } = Path.Combine(root, "cache");
        public string StateDirectory { get; } = Path.Combine(root, "state");
        public string LogsDirectory { get; } = Path.Combine(root, "logs");
        public string ProjectsDirectory { get; } = Path.Combine(root, "projects");
        public string SettingsDirectory => ConfigDirectory;
        public string SettingsFilePath => Path.Combine(ConfigDirectory, "settings.json");
        public string RecentProjectsFilePath => Path.Combine(StateDirectory, "recent.json");
        public void EnsureCreated() { }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public Task<string?> GetTextAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(Text);
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IExternalLauncher
    {
        public string? LastPath { get; private set; }
        public Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default)
        {
            LastPath = path;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeFilePicker : IFilePickerService
    {
        public Task<IReadOnlyList<string>> OpenFilesAsync(
            OpenFilePickerRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> SaveFileAsync(
            SaveFilePickerRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> OpenFolderAsync(
            FolderPickerRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
