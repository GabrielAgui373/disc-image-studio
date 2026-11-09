using System.Globalization;

namespace DiscImageStudio.App.Avalonia.Localization;

/// <summary>
/// The interface languages and how the "system" choice resolves. A Portuguese system gets
/// Brazilian Portuguese; any other language gets English.
/// </summary>
public static class AppLanguage
{
    public const string System = "system";
    public const string English = "en";
    public const string PortugueseBrazil = "pt-BR";

    /// <summary>The choices offered in Settings, in display order.</summary>
    public static IReadOnlyList<string> Choices { get; } = [System, English, PortugueseBrazil];

    /// <summary>The system's interface language, captured before the app changes it.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>A saved preference as one of <see cref="Choices"/>; unknown values mean "system".</summary>
    public static string Normalize(string? language)
    {
        string value = language?.Trim() ?? string.Empty;
        if (value.Equals(English, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        if (value.Equals("pt", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("pt-", StringComparison.OrdinalIgnoreCase))
        {
            return PortugueseBrazil;
        }

        return System;
    }

    /// <summary>The language actually used: <see cref="English"/> or <see cref="PortugueseBrazil"/>.</summary>
    public static string Resolve(string? language, CultureInfo? systemCulture = null)
    {
        string normalized = Normalize(language);
        if (normalized != System)
        {
            return normalized;
        }

        return (systemCulture ?? SystemCulture).TwoLetterISOLanguageName == "pt" ? PortugueseBrazil : English;
    }
}
