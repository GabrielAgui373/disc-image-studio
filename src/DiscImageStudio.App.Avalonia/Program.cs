using Avalonia;
using DiscImageStudio.App.Avalonia.Hosting;

namespace DiscImageStudio.App.Avalonia;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? displayError = DisplayEnvironment.GetStartupError();
        if (displayError is not null)
        {
            Console.Error.WriteLine(displayError);
            return 2;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Disc Image Studio could not start: {exception}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
