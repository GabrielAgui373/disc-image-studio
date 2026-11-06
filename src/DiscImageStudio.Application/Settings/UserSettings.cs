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

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Culture { get; init; } = "en";

    public ApplicationTheme Theme { get; init; } = ApplicationTheme.System;

    public string DefaultProjectDirectory { get; init; } = string.Empty;

    public UserSettings Normalize()
        => this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Culture = NormalizeCulture(Culture),
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
