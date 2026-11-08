using System.Globalization;
using Avalonia.Styling;
using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.App.Avalonia.Hosting;

public static class StartupPreferences
{
    public static void Apply(App app, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        UserSettings normalized = settings.Normalize();
        CultureInfo culture = CultureInfo.GetCultureInfo(normalized.Culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        app.RequestedThemeVariant = normalized.Theme switch
        {
            ApplicationTheme.Light => ThemeVariant.Light,
            ApplicationTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
