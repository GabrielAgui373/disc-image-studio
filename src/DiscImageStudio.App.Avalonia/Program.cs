using Avalonia;
using DiscImageStudio.App.Avalonia.Hosting;

namespace DiscImageStudio.App.Avalonia;

sealed class Program
{
    /// <summary>Set when the user restarts the app from Settings; Main starts it again on exit.</summary>
    internal static bool RestartAfterExit { get; set; }

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
            int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            if (RestartAfterExit)
            {
                StartAgain(args);
            }

            return exitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Disc Image Studio could not start: {exception}");
            return 1;
        }
    }

    private static void StartAgain(string[] args)
    {
        string? process = Environment.ProcessPath;
        if (process is null)
        {
            Console.Error.WriteLine("Disc Image Studio could not restart: the executable path is unknown.");
            return;
        }

        System.Diagnostics.ProcessStartInfo start = new(process) { UseShellExecute = false };
        // Run through the dotnet host ("dotnet DiscImageStudio.App.Avalonia.dll"), pass the app again.
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Disc Image Studio could not restart: {exception.Message}");
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
