using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.Application.Ports;

public interface IAppPaths : IApplicationPaths
{
    string ConfigDirectory { get; }

    string DataDirectory { get; }

    string StateDirectory { get; }

    string LogsDirectory { get; }

    string SettingsFilePath { get; }

    string RecentProjectsFilePath { get; }

    void EnsureCreated();
}

public sealed record FilePickerType(
    string Name,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string>? MimeTypes = null);

public sealed record OpenFilePickerRequest(
    string Title,
    bool AllowMultiple = false,
    string? SuggestedDirectory = null,
    IReadOnlyList<FilePickerType>? FileTypes = null);

public sealed record SaveFilePickerRequest(
    string Title,
    string? SuggestedFileName = null,
    string? SuggestedDirectory = null,
    string? DefaultExtension = null,
    IReadOnlyList<FilePickerType>? FileTypes = null);

public sealed record FolderPickerRequest(string Title, string? SuggestedDirectory = null);

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> OpenFilesAsync(
        OpenFilePickerRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> SaveFileAsync(
        SaveFilePickerRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> OpenFolderAsync(
        FolderPickerRequest request,
        CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    Task<string?> GetTextAsync(CancellationToken cancellationToken = default);

    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

public interface IExternalLauncher
{
    Task<bool> OpenUriAsync(Uri uri, CancellationToken cancellationToken = default);

    Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default);
}

public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public interface IUserPromptService : IUserInteraction
{
    Task<bool> ConfirmShutdownAsync(int activeJobCount, CancellationToken cancellationToken = default);

    Task ShowErrorAsync(
        string title,
        string message,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

public interface IRecentProjectStore
{
    IReadOnlyList<RecentProjectEntry> Load();

    void Record(RecentProjectEntry entry);

    void Remove(string projectPath);

    void Clear();
}

public interface ICacheManager
{
    long GetSizeBytes();

    void Clear();
}

public interface IApplicationLifetime
{
    bool IsShutdownStarted { get; }

    bool IsShutdownReady { get; }

    CancellationToken ShutdownToken { get; }

    Task<bool> RequestShutdownAsync(CancellationToken cancellationToken = default);
}

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public interface IAppLogger
{
    void Log(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null,
        Guid? correlationId = null);
}

public sealed class NullAppLogger : IAppLogger
{
    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null,
        Guid? correlationId = null)
    {
    }
}
