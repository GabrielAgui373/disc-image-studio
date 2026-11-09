using System.Globalization;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>Human-readable sizes in binary units, as the app shows them everywhere.</summary>
public static class ByteSize
{
    public static string Format(long bytes)
        => bytes switch
        {
            >= 1L << 30 => string.Format(CultureInfo.CurrentUICulture, "{0:0.0} GB", bytes / (double)(1L << 30)),
            >= 1L << 20 => string.Format(CultureInfo.CurrentUICulture, "{0:0.0} MB", bytes / (double)(1L << 20)),
            >= 1L << 10 => string.Format(CultureInfo.CurrentUICulture, "{0:0} KB", bytes / (double)(1L << 10)),
            _ => string.Format(CultureInfo.CurrentUICulture, "{0} B", bytes),
        };
}
