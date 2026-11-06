namespace DiscImageStudio.App.Avalonia.Hosting;

public static class DisplayEnvironment
{
    public static string? GetStartupError(
        Func<string, string?>? environment = null,
        bool? isLinux = null)
    {
        if (!(isLinux ?? OperatingSystem.IsLinux()))
        {
            return null;
        }

        environment ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(environment("DISPLAY")))
        {
            return null;
        }

        return "Disc Image Studio needs an X11 or XWayland graphical session. "
               + "The DISPLAY environment variable is missing. Start it from the Fedora desktop session.";
    }
}
