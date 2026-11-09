using System.Globalization;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Settings;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// The application preferences on the Settings page: language, theme, default project folder
/// and the cache. Each change is saved to the settings file at once; nothing waits for a "Save"
/// button. The theme applies immediately, the language when the app restarts. The cache is
/// cleared only while no job runs and no preview holds its source.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IAppPaths _paths;
    private readonly ICacheManager _cache;
    private readonly JobCoordinator _jobs;
    private readonly PreviewSourceCache? _previewSources;
    private readonly IFilePickerService _filePicker;
    private readonly ITextCatalog _text;
    private readonly Action<ThemeVariant> _applyTheme;
    private bool _loading;

    public PreferencesViewModel(
        UserSettings settings,
        ISettingsStore store,
        IAppPaths paths,
        ICacheManager cache,
        JobCoordinator jobs,
        PreviewSourceCache? previewSources,
        IFilePickerService filePicker,
        ITextCatalog text,
        Action<ThemeVariant> applyTheme)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _previewSources = previewSources;
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        Settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        _loading = true;
        Theme = Settings.Theme;
        Language = AppLanguage.Normalize(Settings.Language);
        _loading = false;
    }

    /// <summary>Raised after every saved change, with the new settings.</summary>
    public event Action<UserSettings>? Changed;

    /// <summary>Raised by "Restart now"; the host closes the app the normal way and starts it again.</summary>
    public event Action? RestartRequested;

    public UserSettings Settings { get; private set; }

    public string Title => _text["preferences.title"];

    public string Description => _text["preferences.description"];

    public string LanguageLabel => _text["preferences.language"];

    /// <summary>"System" names the language the system choice resolves to.</summary>
    public string SystemLanguageLabel => string.Format(
        _text.Culture,
        _text["preferences.language.system"],
        LanguageName(AppLanguage.Resolve(AppLanguage.System)));

    public string EnglishLabel => _text["preferences.language.english"];

    public string PortugueseLabel => _text["preferences.language.portuguese"];

    public string LanguageRestartHint => _text["preferences.language.restartHint"];

    public string RestartLabel => _text["preferences.language.restart"];

    public string ThemeLabel => _text["preferences.theme"];

    public string SystemLabel => _text["preferences.theme.system"];

    public string LightLabel => _text["preferences.theme.light"];

    public string DarkLabel => _text["preferences.theme.dark"];

    public string ProjectsLabel => _text["preferences.projects"];

    public string ChooseLabel => _text["preferences.choose"];

    public string ResetLabel => _text["preferences.reset"];

    public string CacheLabel => _text["preferences.cache"];

    public string RefreshCacheLabel => _text["preferences.cache.refresh"];

    public string ClearCacheLabel => _text["preferences.cache.clear"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
    public partial ApplicationTheme Theme { get; private set; }

    public bool IsSystemTheme => Theme == ApplicationTheme.System;

    public bool IsLightTheme => Theme == ApplicationTheme.Light;

    public bool IsDarkTheme => Theme == ApplicationTheme.Dark;

    /// <summary>The saved language choice: "system", "en" or "pt-BR".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemLanguage), nameof(IsEnglish), nameof(IsPortuguese), nameof(NeedsRestart))]
    public partial string Language { get; private set; } = AppLanguage.System;

    public bool IsSystemLanguage => Language == AppLanguage.System;

    public bool IsEnglish => Language == AppLanguage.English;

    public bool IsPortuguese => Language == AppLanguage.PortugueseBrazil;

    /// <summary>True when the saved choice resolves to another language than the one showing.</summary>
    public bool NeedsRestart => AppLanguage.Resolve(Language) != _text.Culture.Name;

    [RelayCommand]
    private void UseSystemLanguage() => SetLanguage(AppLanguage.System);

    [RelayCommand]
    private void UseEnglish() => SetLanguage(AppLanguage.English);

    [RelayCommand]
    private void UsePortuguese() => SetLanguage(AppLanguage.PortugueseBrazil);

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();

    private void SetLanguage(string language)
    {
        if (_loading || Language == language)
        {
            return;
        }

        string previous = Language;
        Language = language;
        if (!Save(Settings with { Language = language }))
        {
            Language = previous;
        }
    }

    private string LanguageName(string language)
        => language == AppLanguage.PortugueseBrazil ? PortugueseLabel : EnglishLabel;

    /// <summary>The folder new projects go to by default.</summary>
    public string ProjectsDirectory => string.IsNullOrWhiteSpace(Settings.DefaultProjectDirectory)
        ? _paths.ProjectsDirectory
        : Settings.DefaultProjectDirectory;

    public bool IsCustomProjectsDirectory => !string.IsNullOrWhiteSpace(Settings.DefaultProjectDirectory);

    [ObservableProperty]
    public partial string CacheText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    [RelayCommand]
    private void UseSystemTheme() => SetTheme(ApplicationTheme.System);

    [RelayCommand]
    private void UseLightTheme() => SetTheme(ApplicationTheme.Light);

    [RelayCommand]
    private void UseDarkTheme() => SetTheme(ApplicationTheme.Dark);

    partial void OnThemeChanged(ApplicationTheme value)
        => _applyTheme(value switch
        {
            ApplicationTheme.Light => ThemeVariant.Light,
            ApplicationTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        });

    private void SetTheme(ApplicationTheme theme)
    {
        if (_loading || Theme == theme)
        {
            return;
        }

        Theme = theme;
        Save(Settings with { Theme = theme });
    }

    [RelayCommand]
    private async Task ChooseProjectsDirectoryAsync()
    {
        string? folder = await _filePicker.OpenFolderAsync(new FolderPickerRequest(_text["preferences.projects.pickerTitle"], ProjectsDirectory));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Save(Settings with { DefaultProjectDirectory = folder });
        }
    }

    [RelayCommand]
    private void ResetProjectsDirectory() => Save(Settings with { DefaultProjectDirectory = string.Empty });

    /// <summary>Measures the cache folder in the background.</summary>
    [RelayCommand]
    public async Task RefreshCacheAsync()
    {
        IsWorking = true;
        try
        {
            long bytes = await Task.Run(_cache.GetSizeBytes);
            CacheText = string.Format(CultureInfo.CurrentUICulture, _text["preferences.cache.size"], ByteSize.Format(bytes), _paths.CacheDirectory);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        // Exports and calibration targets write their temporary sources here; previews read theirs.
        if (_jobs.RunningJobIds.Count > 0 || _previewSources?.Forget() == false)
        {
            StatusText = _text["preferences.cache.busy"];
            return;
        }

        IsWorking = true;
        try
        {
            await Task.Run(_cache.Clear);
            StatusText = _text["preferences.cache.cleared"];
        }
        finally
        {
            IsWorking = false;
        }

        await RefreshCacheAsync();
    }

    private bool Save(UserSettings settings)
    {
        UserSettings normalized = settings.Normalize();
        try
        {
            _store.Save(normalized);
            Settings = normalized;
            StatusText = string.Empty;
            OnPropertyChanged(nameof(ProjectsDirectory));
            OnPropertyChanged(nameof(IsCustomProjectsDirectory));
            Changed?.Invoke(normalized);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = string.Format(CultureInfo.CurrentUICulture, _text["preferences.saveFailed"], exception.Message);
            return false;
        }
    }
}
