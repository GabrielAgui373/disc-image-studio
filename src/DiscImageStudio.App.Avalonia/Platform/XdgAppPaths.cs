using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class XdgAppPaths : IAppPaths
{
    private const string ApplicationDirectoryName = "disc-image-studio";
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead
                                                        | UnixFileMode.UserWrite
                                                        | UnixFileMode.UserExecute;

    public XdgAppPaths(
        Func<string, string?> environment,
        string homeDirectory,
        string documentsDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string home = Path.GetFullPath(homeDirectory);

        ConfigDirectory = AppDirectory(environment("XDG_CONFIG_HOME"), Path.Combine(home, ".config"));
        DataDirectory = AppDirectory(environment("XDG_DATA_HOME"), Path.Combine(home, ".local", "share"));
        CacheDirectory = AppDirectory(environment("XDG_CACHE_HOME"), Path.Combine(home, ".cache"));
        StateDirectory = AppDirectory(environment("XDG_STATE_HOME"), Path.Combine(home, ".local", "state"));
        LogsDirectory = Path.Combine(StateDirectory, "logs");
        SettingsDirectory = ConfigDirectory;
        ProjectsDirectory = string.IsNullOrWhiteSpace(documentsDirectory)
            ? Path.Combine(DataDirectory, "projects")
            : Path.Combine(Path.GetFullPath(documentsDirectory), "Disc Image Studio");
        SettingsFilePath = Path.Combine(ConfigDirectory, "settings.json");
        RecentProjectsFilePath = Path.Combine(StateDirectory, "recent-projects.json");
    }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string StateDirectory { get; }

    public string LogsDirectory { get; }

    public string ProjectsDirectory { get; }

    public string SettingsDirectory { get; }

    public string SettingsFilePath { get; }

    public string RecentProjectsFilePath { get; }

    public static XdgAppPaths CreateDefault()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME")
                ?? throw new InvalidOperationException("The user home directory could not be resolved.");
        }

        return new XdgAppPaths(
            Environment.GetEnvironmentVariable,
            home,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    public void EnsureCreated()
    {
        foreach (string directory in new[]
                 {
                     ConfigDirectory,
                     DataDirectory,
                     CacheDirectory,
                     StateDirectory,
                     LogsDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, PrivateDirectoryMode);
            }
        }
    }

    private static string AppDirectory(string? value, string fallback)
    {
        string root = !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(fallback);
        return Path.Combine(root, ApplicationDirectoryName);
    }
}
