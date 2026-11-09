using System.Globalization;
using Avalonia.Styling;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.App.Avalonia.Hosting;

public static class StartupPreferences
{
    /// <summary>
    /// Applies the saved preferences before any window exists and returns the interface
    /// language. The interface language follows the "Language" preference (by default the
    /// system's); the formatting culture keeps the separate "Culture" setting, so file formats
    /// and engineering values never change with the language.
    /// </summary>
    public static CultureInfo Apply(App app, UserSettings settings, CultureInfo? systemCulture = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        UserSettings normalized = settings.Normalize();
        CultureInfo culture = CultureInfo.GetCultureInfo(normalized.Culture);
        CultureInfo language = CultureInfo.GetCultureInfo(AppLanguage.Resolve(normalized.Language, systemCulture));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = language;
        app.RequestedThemeVariant = normalized.Theme switch
        {
            ApplicationTheme.Light => ThemeVariant.Light,
            ApplicationTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        return language;
    }
}
