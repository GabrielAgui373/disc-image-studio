using System.Globalization;

namespace DiscImageStudio.Application.Settings;

public enum ApplicationTheme
{
    System,
    Light,
    Dark,
}

public sealed record UserSettings
{
    public const int CurrentSchemaVersion = 1;

    public const string SystemLanguage = "system";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Formatting culture for numbers and dates that are not interface text.</summary>
    public string Culture { get; init; } = "en";

    /// <summary>Interface language: "system" (follow the system) or a language tag such as "pt-BR".</summary>
    public string Language { get; init; } = SystemLanguage;

    public ApplicationTheme Theme { get; init; } = ApplicationTheme.System;

    public string DefaultProjectDirectory { get; init; } = string.Empty;

    public UserSettings Normalize()
        => this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Culture = NormalizeCulture(Culture),
            Language = string.IsNullOrWhiteSpace(Language) ? SystemLanguage : Language.Trim(),
            Theme = Enum.IsDefined(Theme) ? Theme : ApplicationTheme.System,
            DefaultProjectDirectory = NormalizeDirectory(DefaultProjectDirectory),
        };

    private static string NormalizeCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return "en";
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            return "en";
        }
    }

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }
}

public sealed record RecentProjectEntry(
    string Name,
    string DiscFamily,
    string ProjectPath,
    DateTimeOffset LastOpenedUtc,
    string? ThumbnailPngBase64 = null);
