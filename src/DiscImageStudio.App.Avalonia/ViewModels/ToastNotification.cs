namespace DiscImageStudio.App.Avalonia.ViewModels;

public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ToastNotification(
    Guid Id,
    string Title,
    string Message,
    ToastSeverity Severity,
    TimeSpan Lifetime);
