using System.Reflection;
using System.Runtime.InteropServices;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Diagnostics;

public sealed record AboutInfo(
    string ProductVersion,
    string Commit,
    string DotnetRuntime,
    string AvaloniaVersion,
    string OperatingSystem,
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory,
    string LogsDirectory)
{
    public static AboutInfo Create(IAppPaths paths)
    {
        Assembly assembly = typeof(App).Assembly;
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        string commit = informationalVersion.Contains('+', StringComparison.Ordinal)
            ? informationalVersion[(informationalVersion.IndexOf('+', StringComparison.Ordinal) + 1)..]
            : "development";

        return new AboutInfo(
            informationalVersion.Split('+')[0],
            commit,
            RuntimeInformation.FrameworkDescription,
            typeof(global::Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription,
            paths.ConfigDirectory,
            paths.DataDirectory,
            paths.CacheDirectory,
            paths.LogsDirectory);
    }
}
