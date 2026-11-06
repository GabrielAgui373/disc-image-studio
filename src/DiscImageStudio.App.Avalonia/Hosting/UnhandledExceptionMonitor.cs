using Avalonia.Threading;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Hosting;

public sealed class UnhandledExceptionMonitor : IDisposable
{
    private readonly IAppLogger _logger;
    private bool _disposed;

    public UnhandledExceptionMonitor(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Exception exception = args.ExceptionObject as Exception
            ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        _logger.Log(
            AppLogLevel.Critical,
            "exception.app-domain.unhandled",
            args.IsTerminating ? "A fatal unhandled exception is terminating the application." : "An unhandled exception occurred.",
            exception,
            Guid.NewGuid());
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        _logger.Log(
            AppLogLevel.Error,
            "exception.task.unobserved",
            "An unobserved task exception was detected.",
            args.Exception,
            Guid.NewGuid());
        args.SetObserved();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        _logger.Log(
            AppLogLevel.Critical,
            "exception.dispatcher.unhandled",
            "An unhandled UI exception occurred.",
            args.Exception,
            Guid.NewGuid());
        args.Handled = false;
    }
}
