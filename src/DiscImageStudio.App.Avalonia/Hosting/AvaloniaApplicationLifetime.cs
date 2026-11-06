using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.App.Avalonia.Hosting;

public sealed class AvaloniaApplicationLifetime : IApplicationLifetime, IDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    private readonly JobCoordinator _jobs;
    private readonly IUserPromptService _prompts;
    private readonly ISettingsStore _settingsStore;
    private readonly ITextCatalog _text;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private UserSettings _settings;

    public AvaloniaApplicationLifetime(
        JobCoordinator jobs,
        IUserPromptService prompts,
        ISettingsStore settingsStore,
        UserSettings settings,
        ITextCatalog text,
        IAppLogger logger)
    {
        _jobs = jobs;
        _prompts = prompts;
        _settingsStore = settingsStore;
        _settings = settings;
        _text = text;
        _logger = logger;
    }

    public bool IsShutdownStarted { get; private set; }

    public bool IsShutdownReady { get; private set; }

    public CancellationToken ShutdownToken => _shutdown.Token;

    public void UpdateSettings(UserSettings settings) => _settings = settings.Normalize();

    public async Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _shutdownGate.WaitAsync(cancellationToken);
        try
        {
            if (IsShutdownReady)
            {
                return true;
            }

            int activeJobs = _jobs.RunningJobIds.Count;
            if (activeJobs > 0 && !await _prompts.ConfirmShutdownAsync(activeJobs, cancellationToken))
            {
                return false;
            }

            IsShutdownStarted = true;
            _shutdown.Cancel();
            bool cleaned = await _jobs.ShutdownAsync(CleanupTimeout, cancellationToken);
            if (!cleaned)
            {
                _logger.Log(
                    AppLogLevel.Warning,
                    "shutdown.cleanup.timeout",
                    "Active jobs did not finish cleanup within 10 seconds.");
                await _prompts.ShowErrorAsync(
                    _text["shutdown.timeout.title"],
                    _text["shutdown.timeout.message"],
                    cancellationToken: cancellationToken);
                return false;
            }

            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.Log(
                    AppLogLevel.Error,
                    "settings.shutdown.save.failed",
                    "Settings could not be persisted during shutdown.",
                    exception);
            }

            IsShutdownReady = true;
            return true;
        }
        finally
        {
            _shutdownGate.Release();
        }
    }

    public void Dispose()
    {
        _shutdown.Dispose();
        _shutdownGate.Dispose();
    }
}
