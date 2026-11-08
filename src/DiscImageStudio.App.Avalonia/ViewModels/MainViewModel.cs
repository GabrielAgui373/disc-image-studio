using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Navigation;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase, IOverlayPromptPresenter
{
    private static readonly FilePickerType ProjectFileType = new(
        "Disc Image Studio project",
        ["*.discproj"],
        ["application/json"]);

    private readonly IAppPaths _paths;
    private readonly IFilePickerService _filePicker;
    private readonly IFileSystem _fileSystem;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLauncher _launcher;
    private readonly ITextCatalog _text;
    private readonly AboutInfo _about;
    private readonly ProjectLifecycleService _projects;
    private readonly JobCoordinator _jobs;
    private readonly IUiDispatcher _dispatcher;
    private readonly UserSettings _settings;
    private readonly ProjectAssetService _assets;
    private readonly ProjectCompositionService _compositions;
    private readonly ProjectEditor _editor;
    private readonly UserPresetCatalog _presetCatalog;
    private IReadOnlyList<ProjectPresetDefinition> _userPresets = [];
    private readonly List<IDisposable> _eventSubscriptions = [];
    private TaskCompletionSource<int>? _promptCompletion;
    private CancellationTokenRegistration _promptCancellation;

    public MainViewModel(
        IAppPaths paths,
        IFilePickerService filePicker,
        IFileSystem fileSystem,
        IClipboardService clipboard,
        IExternalLauncher launcher,
        ITextCatalog text,
        AboutInfo about,
        ProjectLifecycleService projects,
        JobCoordinator jobs,
        IUiDispatcher dispatcher,
        IApplicationEventStream applicationEvents,
        UserSettings settings,
        RecentProjectsService recentProjects,
        IThumbnailDecoder thumbnails,
        ProjectAssetService assets,
        UserPresetCatalog presets,
        ProjectCompositionService compositions,
        ProjectEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _compositions = compositions ?? throw new ArgumentNullException(nameof(compositions));
        Preview = new CompositionPreviewViewModel(_compositions, projects, text);
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _presetCatalog = presets ?? throw new ArgumentNullException(nameof(presets));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _about = about ?? throw new ArgumentNullException(nameof(about));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Normalize();
        ArgumentNullException.ThrowIfNull(applicationEvents);

        Title = text["app.title"];
        StatusTitle = text["host.status.title"];
        StatusDescription = text["host.status.description"];
        AboutTitle = text["about.title"];
        VersionLabel = text["about.version"];
        CommitLabel = text["about.commit"];
        RuntimeLabel = text["about.runtime"];
        AvaloniaLabel = text["about.avalonia"];
        OperatingSystemLabel = text["about.operatingSystem"];
        ConfigDirectoryLabel = text["about.configDirectory"];
        DataDirectoryLabel = text["about.dataDirectory"];
        CacheDirectoryLabel = text["about.cacheDirectory"];
        LogsDirectoryLabel = text["about.logsDirectory"];
        CopyDiagnosticsLabel = text["action.copyDiagnostics"];
        OpenLogsLabel = text["action.openLogs"];
        JobDockTitle = text["jobDock.title"];
        JobDockEmptyLabel = text["jobDock.empty"];
        JobDockCancelLabel = text["jobDock.cancel"];
        JobDockClearLabel = text["jobDock.clear"];
        JobDockDetailsLabel = text["jobDock.details"];
        HomeHeroEyebrow = text["home.hero.eyebrow"];
        HomeHeroTitle = text["home.hero.title"];
        HomeHeroDescription = text["home.hero.description"];
        NewProjectLabel = text["home.action.newProject"];
        OpenProjectLabel = text["home.action.openProject"];
        ProjectSectionLabel = text["navigation.projectSection"];
        ContinueEyebrow = text["home.continue.eyebrow"];
        ContinueInDesignLabel = text["home.continue.action"];
        SettingsPreferencesNote = text["settings.preferences.note"];
        RecentProjectsTitle = text["home.recent.title"];
        RecentProjectsDescription = text["home.recent.description"];
        RecentProjectsEmptyTitle = text["home.recent.emptyTitle"];
        RecentProjectsEmptyDescription = text["home.recent.emptyDescription"];
        PlatformDetailsTitle = text["home.platform.title"];
        PlatformDetailsDescription = text["home.platform.description"];
        NewProjectTitle = text["newProject.title"];
        NewProjectDescription = text["newProject.description"];
        NewProjectNameLabel = text["newProject.name.label"];
        NewProjectNameWatermark = text["newProject.name.watermark"];
        NewProjectDirectoryLabel = text["newProject.directory.label"];
        NewProjectDirectoryHint = text["newProject.directory.hint"];
        NewProjectBrowseLabel = text["newProject.browse"];
        NewProjectFamilyLabel = text["newProject.family.label"];
        NewProjectFamilyHint = text["newProject.family.hint"];
        NewProjectPresetLabel = text["newProject.preset.label"];
        NewProjectAdvancedLabel = text["newProject.advanced.label"];
        NewProjectVolumeLabelText = text["newProject.volume.label"];
        NewProjectOuterRingLabel = text["newProject.outerRing.label"];
        NewProjectFileLabel = text["newProject.file.label"];
        CreateProjectLabel = text["newProject.create"];
        CancelLabel = text["prompt.cancel"];
        CloseDialogLabel = text["dialog.close"];
        SaveProjectLabel = text["project.action.save"];
        SaveProjectAsLabel = text["project.action.saveAs"];
        CloseProjectLabel = text["project.action.close"];
        ProjectFileLabel = text["project.file.label"];
        ProjectDirtyLabel = text["project.dirty"];

        HomeTemplates =
        [
            new HomeTemplateOption(
                "cd",
                text["home.template.cd.title"],
                text["home.template.cd.description"],
                text["home.template.cd.capacity"],
                "CD",
                text["home.template.selectedBadge"],
                OpticalDiscFamily.CompactDisc),
            new HomeTemplateOption(
                "dvd",
                text["home.template.dvd.title"],
                text["home.template.dvd.description"],
                text["home.template.dvd.capacity"],
                "DVD",
                text["home.template.selectedBadge"],
                OpticalDiscFamily.Dvd),
            new HomeTemplateOption(
                "bd",
                text["home.template.bd.title"],
                text["home.template.bd.description"],
                text["home.template.bd.capacity"],
                "BD",
                text["home.template.selectedBadge"],
                OpticalDiscFamily.BluRay),
        ];
        ProjectPresets = CreateProjectPresets(text);
        NewProjectDirectory = string.IsNullOrWhiteSpace(_settings.DefaultProjectDirectory)
            ? paths.ProjectsDirectory
            : _settings.DefaultProjectDirectory;
        NewProjectVolumeLabel = text["newProject.defaultVolumeLabel"];
        SelectHomeTemplate(HomeTemplates[0]);

        HomeNavigation = CreateNavigationItem(WorkspacePage.Home, "navigation.home", "Alt+H");
        DesignNavigation = CreateNavigationItem(WorkspacePage.Design, "navigation.design", "Alt+D");
        PreviewNavigation = CreateNavigationItem(WorkspacePage.Preview, "navigation.preview", "Alt+P");
        ExportNavigation = CreateNavigationItem(WorkspacePage.Export, "navigation.export", "Alt+E");
        BurnNavigation = CreateNavigationItem(WorkspacePage.Burn, "navigation.burn", "Alt+B");
        SettingsNavigation = CreateNavigationItem(WorkspacePage.Settings, "navigation.settings", "Alt+S");
        NavigationItems = [
            HomeNavigation,
            DesignNavigation,
            PreviewNavigation,
            ExportNavigation,
            BurnNavigation,
        ];

        RefreshNavigation();

        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJobHistory));
        _eventSubscriptions.Add(applicationEvents.Subscribe<JobStarted>(eventData => _dispatcher.Post(() => OnJobStarted(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<JobProgressReported>(eventData => _dispatcher.Post(() => OnJobProgress(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<JobFinished>(eventData => _dispatcher.Post(() => OnJobFinished(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<ProjectOpened>(eventData => _dispatcher.Post(() => OnProjectOpened(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<ProjectSaved>(eventData => _dispatcher.Post(() => OnProjectSaved(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<ProjectClosed>(eventData => _dispatcher.Post(() => OnProjectClosed(eventData))));
        _eventSubscriptions.Add(applicationEvents.Subscribe<ProjectDirtyChanged>(eventData => _dispatcher.Post(() => OnProjectDirtyChanged(eventData))));
        RefreshProjectState();

        Recent = new RecentProjectsViewModel(
            recentProjects ?? throw new ArgumentNullException(nameof(recentProjects)),
            thumbnails ?? throw new ArgumentNullException(nameof(thumbnails)),
            text,
            OpenProjectAsync);
        _ = Recent.RefreshAsync();

        ProjectSettings = new ProjectSettingsViewModel(
            projects,
            _assets,
            _presetCatalog,
            filePicker,
            text,
            family => ProjectPresets.Where(preset => preset.Family == family).ToArray(),
            _editor);
        ProjectSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectSettingsViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsAnyDialogOpen));
            }
        };
        ProjectSettingsLabel = text["project.action.settings"];
        UndoLabel = text["history.undo"];
        RedoLabel = text["history.redo"];
        _eventSubscriptions.Add(applicationEvents.Subscribe<ProjectWorkspaceChanged>(eventData => _dispatcher.Post(() => OnProjectWorkspaceChanged(eventData))));
        _editor.HistoryChanged += (_, _) => _dispatcher.Post(RefreshHistoryState);
        RefreshHistoryState();
    }

    public string UndoLabel { get; }

    public string RedoLabel { get; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    [ObservableProperty]
    public partial bool CanRedo { get; private set; }

    [ObservableProperty]
    public partial string UndoToolTip { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RedoToolTip { get; private set; } = string.Empty;

    [RelayCommand]
    private void Undo() => RunHistory(_editor.Undo, "history.undone");

    [RelayCommand]
    private void Redo() => RunHistory(_editor.Redo, "history.redone");

    private void RunHistory(Func<ApplicationResult<EditOutcome>> step, string statusKey)
    {
        // Window shortcuts must not change the project behind a dialog or during a file operation.
        if (!HasOpenProject || IsAnyDialogOpen || IsProjectOperationInProgress)
        {
            return;
        }

        ApplicationResult<EditOutcome> result = step();
        if (result.Succeeded)
        {
            ProjectActionError = null;
            ProjectActionStatus = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _text[statusKey],
                HistoryText(result.Value.Label));
        }

        RefreshHistoryState();
    }

    private void RefreshHistoryState()
    {
        CanUndo = _editor.CanUndo;
        CanRedo = _editor.CanRedo;
        UndoToolTip = _editor.UndoLabel is { } undo
            ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, _text["history.undoTip"], HistoryText(undo))
            : UndoLabel;
        RedoToolTip = _editor.RedoLabel is { } redo
            ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, _text["history.redoTip"], HistoryText(redo))
            : RedoLabel;
    }

    private string HistoryText(string label) => _text[$"history.label.{label}"];

    private void OnProjectWorkspaceChanged(ProjectWorkspaceChanged change)
    {
        RefreshProjectState();
        RefreshHistoryState();
        if (!change.IsPreview)
        {
            // A committed change may have touched layers or geometry; recompose the preview.
            _ = Preview.RefreshAsync();
        }
    }

    public RecentProjectsViewModel Recent { get; }

    public CompositionPreviewViewModel Preview { get; }

    public ProjectSettingsViewModel ProjectSettings { get; }

    public string ProjectSettingsLabel { get; }

    public string Title { get; }
    public string StatusTitle { get; }
    public string StatusDescription { get; }
    public string AboutTitle { get; }
    public string VersionLabel { get; }
    public string CommitLabel { get; }
    public string RuntimeLabel { get; }
    public string AvaloniaLabel { get; }
    public string OperatingSystemLabel { get; }
    public string ConfigDirectoryLabel { get; }
    public string DataDirectoryLabel { get; }
    public string CacheDirectoryLabel { get; }
    public string LogsDirectoryLabel { get; }
    public string CopyDiagnosticsLabel { get; }
    public string OpenLogsLabel { get; }
    public string JobDockTitle { get; }
    public string JobDockEmptyLabel { get; }
    public string JobDockCancelLabel { get; }
    public string JobDockClearLabel { get; }
    public string JobDockDetailsLabel { get; }
    public string HomeHeroEyebrow { get; }
    public string HomeHeroTitle { get; }
    public string HomeHeroDescription { get; }
    public string NewProjectLabel { get; }
    public string OpenProjectLabel { get; }
    public string ProjectSectionLabel { get; }
    public string ContinueEyebrow { get; }
    public string ContinueInDesignLabel { get; }
    public string SettingsPreferencesNote { get; }
    public string RecentProjectsTitle { get; }
    public string RecentProjectsDescription { get; }
    public string RecentProjectsEmptyTitle { get; }
    public string RecentProjectsEmptyDescription { get; }
    public string PlatformDetailsTitle { get; }
    public string PlatformDetailsDescription { get; }
    public string NewProjectTitle { get; }
    public string NewProjectDescription { get; }
    public string NewProjectNameLabel { get; }
    public string NewProjectNameWatermark { get; }
    public string NewProjectDirectoryLabel { get; }
    public string NewProjectDirectoryHint { get; }
    public string NewProjectBrowseLabel { get; }
    public string NewProjectFamilyLabel { get; }
    public string NewProjectFamilyHint { get; }
    public string NewProjectPresetLabel { get; }
    public string NewProjectAdvancedLabel { get; }
    public string NewProjectVolumeLabelText { get; }
    public string NewProjectOuterRingLabel { get; }
    public string NewProjectFileLabel { get; }
    public string CreateProjectLabel { get; }
    public string CancelLabel { get; }
    public string CloseDialogLabel { get; }
    public string SaveProjectLabel { get; }
    public string SaveProjectAsLabel { get; }
    public string CloseProjectLabel { get; }
    public string ProjectFileLabel { get; }
    public string ProjectDirtyLabel { get; }
    public WorkspaceNavigationItem HomeNavigation { get; }
    public WorkspaceNavigationItem DesignNavigation { get; }
    public WorkspaceNavigationItem PreviewNavigation { get; }
    public WorkspaceNavigationItem ExportNavigation { get; }
    public WorkspaceNavigationItem BurnNavigation { get; }
    public WorkspaceNavigationItem SettingsNavigation { get; }
    public IReadOnlyList<WorkspaceNavigationItem> NavigationItems { get; }
    public IReadOnlyList<HomeTemplateOption> HomeTemplates { get; }
    public IReadOnlyList<NewProjectPresetOption> ProjectPresets { get; }
    public ObservableCollection<NewProjectPresetOption> NewProjectPresets { get; } = [];
    public ObservableCollection<JobDockItem> Jobs { get; } = [];
    public ObservableCollection<ToastNotification> Toasts { get; } = [];
    public bool HasJobHistory => Jobs.Count > 0;
    public string ProductVersion => _about.ProductVersion;
    public string Commit => _about.Commit;
    public string DotnetRuntime => _about.DotnetRuntime;
    public string AvaloniaVersion => _about.AvaloniaVersion;
    public string OperatingSystem => _about.OperatingSystem;
    public string ConfigDirectory => _about.ConfigDirectory;
    public string DataDirectory => _about.DataDirectory;
    public string CacheDirectory => _about.CacheDirectory;
    public string LogsDirectory => _about.LogsDirectory;

    [ObservableProperty]
    public partial string CurrentPageTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentPageDescription { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentPageBadge { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? NavigationNotice { get; private set; }

    public bool HasNavigationNotice => !string.IsNullOrWhiteSpace(NavigationNotice);

    [ObservableProperty]
    public partial bool IsHomePage { get; private set; }

    [ObservableProperty]
    public partial bool IsPlaceholderPage { get; private set; }

    /// <summary>Design, Preview, Export or Burn with a project open: the project bar is shown.</summary>
    [ObservableProperty]
    public partial bool IsProjectPage { get; private set; }

    [ObservableProperty]
    public partial bool IsSettingsPage { get; private set; }

    /// <summary>Family token of the open project for its disc artwork ("cd", "dvd" or "bd").</summary>
    [ObservableProperty]
    public partial string ProjectFamilyVariant { get; private set; } = "cd";

    public bool HasPageBadge => !string.IsNullOrEmpty(CurrentPageBadge);

    public bool HasHomeActionStatus => !string.IsNullOrWhiteSpace(HomeActionStatus);

    partial void OnCurrentPageBadgeChanged(string value) => OnPropertyChanged(nameof(HasPageBadge));

    partial void OnHomeActionStatusChanged(string value) => OnPropertyChanged(nameof(HasHomeActionStatus));


    [ObservableProperty]

    public partial bool IsDesignPage { get; private set; }

    [ObservableProperty]
    public partial bool IsJobDockExpanded { get; private set; }

    [ObservableProperty]
    public partial OverlayPromptRequest? ActivePrompt { get; private set; }

    [ObservableProperty]
    public partial bool IsModalOpen { get; private set; }

    public string ModalTitle => ActivePrompt?.Title ?? string.Empty;
    public string ModalMessage => ActivePrompt?.Message ?? string.Empty;
    public string ModalPrimaryLabel => ActivePrompt?.PrimaryLabel ?? string.Empty;
    public string ModalSecondaryLabel => ActivePrompt?.SecondaryLabel ?? string.Empty;
    public string ModalTertiaryLabel => ActivePrompt?.TertiaryLabel ?? string.Empty;
    public bool HasModalSecondary => !string.IsNullOrWhiteSpace(ActivePrompt?.SecondaryLabel);
    public bool HasModalTertiary => !string.IsNullOrWhiteSpace(ActivePrompt?.TertiaryLabel);

    partial void OnNavigationNoticeChanged(string? value)
        => OnPropertyChanged(nameof(HasNavigationNotice));

    partial void OnActivePromptChanged(OverlayPromptRequest? value)
    {
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ModalMessage));
        OnPropertyChanged(nameof(ModalPrimaryLabel));
        OnPropertyChanged(nameof(ModalSecondaryLabel));
        OnPropertyChanged(nameof(ModalTertiaryLabel));
        OnPropertyChanged(nameof(HasModalSecondary));
        OnPropertyChanged(nameof(HasModalTertiary));
    }

    [ObservableProperty]
    public partial string? ActionStatus { get; private set; }

    [ObservableProperty]
    public partial HomeTemplateOption? SelectedHomeTemplate { get; private set; }

    [ObservableProperty]
    public partial string HomeActionStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasOpenProject { get; private set; }

    [ObservableProperty]
    public partial bool IsProjectDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsProjectOperationInProgress { get; private set; }

    [ObservableProperty]
    public partial string ProjectName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectFilePath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? ProjectActionStatus { get; private set; }

    [ObservableProperty]
    public partial string? ProjectActionError { get; private set; }

    public bool HasProjectActionStatus => !string.IsNullOrWhiteSpace(ProjectActionStatus);

    public bool HasProjectActionError => !string.IsNullOrWhiteSpace(ProjectActionError);

    public bool HasProjectSurface => HasOpenProject || HasProjectActionStatus || HasProjectActionError;

    /// <summary>
    /// Window-level shortcuts (Ctrl+O/S/W) must not act on the project behind a dialog.
    /// </summary>
    public bool IsAnyDialogOpen => IsModalOpen || IsNewProjectDialogOpen || ProjectSettings.IsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingAssets), nameof(MissingAssetsLabel))]
    public partial int MissingAssetCount { get; private set; }

    public bool HasMissingAssets => MissingAssetCount > 0;

    public string MissingAssetsLabel => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        _text[MissingAssetCount == 1 ? "project.missingAssets.one" : "project.missingAssets.many"],
        MissingAssetCount);

    [ObservableProperty]
    public partial bool IsNewProjectDialogOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsNewProjectAdvancedOpen { get; set; }

    [ObservableProperty]
    public partial bool IsCreatingProject { get; private set; }

    [ObservableProperty]
    public partial string NewProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProjectDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewProjectVolumeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewProjectFilesAtOuterRing { get; set; }

    [ObservableProperty]
    public partial string NewProjectFilePathPreview { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? NewProjectError { get; private set; }

    public bool HasNewProjectError => !string.IsNullOrWhiteSpace(NewProjectError);

    [ObservableProperty]
    public partial NewProjectPresetOption? SelectedProjectPreset { get; set; }

    partial void OnNewProjectErrorChanged(string? value)
        => OnPropertyChanged(nameof(HasNewProjectError));

    partial void OnProjectActionErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProjectActionError));
        OnPropertyChanged(nameof(HasProjectSurface));
    }

    partial void OnProjectActionStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProjectActionStatus));
        OnPropertyChanged(nameof(HasProjectSurface));
    }

    partial void OnHasOpenProjectChanged(bool value)
        => OnPropertyChanged(nameof(HasProjectSurface));

    partial void OnNewProjectNameChanged(string value) => UpdateNewProjectFilePathPreview();

    partial void OnNewProjectDirectoryChanged(string value) => UpdateNewProjectFilePathPreview();

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        await _clipboard.SetTextAsync(BuildDiagnostics());
        ActionStatus = _text["action.diagnosticsCopied"];
    }

    [RelayCommand]
    private async Task OpenLogsAsync()
    {
        bool opened = await _launcher.OpenPathAsync(_paths.LogsDirectory);
        ActionStatus = opened ? _text["action.logsOpened"] : _text["action.logsOpenFailed"];
    }

    [RelayCommand]
    private void StartNewProject()
    {
        if (IsModalOpen)
        {
            return;
        }

        _userPresets = _presetCatalog.LoadUserPresets();
        SelectHomeTemplate(SelectedHomeTemplate ?? HomeTemplates[0]);
        NewProjectError = null;
        IsNewProjectDialogOpen = true;
        UpdateNewProjectFilePathPreview();
    }

    [RelayCommand]
    private async Task StartOpenProjectAsync()
    {
        if (IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return;
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = await _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
                _text["openProject.title"],
                SuggestedDirectory: GetSuggestedProjectDirectory(),
                FileTypes: [ProjectFileType]));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProjectActionError = _text["openProject.error.pickerUnavailable"];
            return;
        }

        string? path = paths.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenProjectAsync(path);
        }
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (!HasOpenProject || IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return;
        }

        await PersistProjectAsync(() => _projects.SaveProjectAsync(), isSaveAs: false);
    }

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        if (!HasOpenProject || IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return;
        }

        string? path;
        try
        {
            path = await _filePicker.SaveFileAsync(new SaveFilePickerRequest(
                _text["saveAs.title"],
                SuggestedFileName: GetSuggestedProjectFileName(),
                SuggestedDirectory: GetSuggestedProjectDirectory(),
                DefaultExtension: ProjectJsonStore.FileExtension.TrimStart('.'),
                FileTypes: [ProjectFileType]));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ProjectActionError = _text["saveAs.error.pickerUnavailable"];
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await PersistProjectAsync(
            () => _projects.SaveProjectAsAsync(EnsureProjectFileExtension(path)),
            isSaveAs: true);
    }

    [RelayCommand]
    private async Task OpenProjectSettingsAsync()
    {
        if (!HasOpenProject || IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return;
        }

        await ProjectSettings.OpenAsync();
    }

    [RelayCommand]
    private async Task CloseProjectAsync()
    {
        if (!HasOpenProject || IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return;
        }

        IsProjectOperationInProgress = true;
        ProjectActionError = null;
        try
        {
            ApplicationResult result = await Task.Run(() => _projects.CloseProjectAsync());
            if (!result.Succeeded)
            {
                ProjectActionError = result.Error?.Code == ApplicationErrorCode.UserCanceled
                    ? _text["project.error.operationCanceled"]
                    : _text["project.error.closeFailed"];
                return;
            }

            // The ProjectClosed event handler publishes the formatted status message.
        }
        finally
        {
            IsProjectOperationInProgress = false;
            RefreshProjectState();
            RefreshNavigation();
        }
    }

    [RelayCommand]
    private void CancelNewProject()
    {
        if (IsCreatingProject)
        {
            return;
        }

        NewProjectError = null;
        IsNewProjectDialogOpen = false;
    }

    [RelayCommand]
    private async Task BrowseNewProjectDirectoryAsync()
    {
        try
        {
            string? directory = await _filePicker.OpenFolderAsync(
                new FolderPickerRequest(NewProjectDirectoryLabel, NewProjectDirectory));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                NewProjectDirectory = directory;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            NewProjectError = _text["newProject.error.directoryUnavailable"];
        }
    }

    [RelayCommand]
    private async Task CreateNewProjectAsync()
    {
        if (IsCreatingProject)
        {
            return;
        }

        if (!TryBuildNewProjectRequest(out CreateProjectRequest request))
        {
            return;
        }

        IsCreatingProject = true;
        NewProjectError = null;
        try
        {
            ApplicationResult<ProjectWorkspace> result = await _projects.CreateProjectAsync(request);
            if (!result.Succeeded)
            {
                NewProjectError = CreateProjectError(result.Error?.Code);
                return;
            }

            IsNewProjectDialogOpen = false;
            HomeActionStatus = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _text["newProject.created"],
                result.Value.Identity.Name);
            RefreshProjectState();
            RefreshNavigation();
        }
        finally
        {
            IsCreatingProject = false;
        }
    }

    [RelayCommand]
    private void SelectHomeTemplate(HomeTemplateOption? template)
    {
        if (template is null)
        {
            return;
        }

        foreach (HomeTemplateOption candidate in HomeTemplates)
        {
            candidate.IsSelected = ReferenceEquals(candidate, template);
        }

        SelectedHomeTemplate = template;
        RefreshProjectPresets();
    }

    [RelayCommand]
    private void NavigateHome() => NavigateTo(WorkspacePage.Home);

    [RelayCommand]
    private void NavigateDesign() => NavigateTo(WorkspacePage.Design);

    [RelayCommand]
    private void NavigatePreview() => NavigateTo(WorkspacePage.Preview);

    [RelayCommand]
    private void NavigateExport() => NavigateTo(WorkspacePage.Export);

    [RelayCommand]
    private void NavigateBurn() => NavigateTo(WorkspacePage.Burn);

    [RelayCommand]
    private void NavigateSettings() => NavigateTo(WorkspacePage.Settings);

    [RelayCommand]
    private void ToggleJobDock() => IsJobDockExpanded = !IsJobDockExpanded;

    [RelayCommand]
    private void CancelJob(Guid jobId)
    {
        _ = _jobs.CancelJob(jobId);
    }

    [RelayCommand]
    private void ClearCompletedJobs()
    {
        foreach (JobDockItem item in Jobs.Where(item => !item.IsRunning).ToArray())
        {
            Jobs.Remove(item);
        }
    }

    [RelayCommand]
    private void DismissToast(ToastNotification? toast)
    {
        if (toast is not null)
        {
            Toasts.Remove(toast);
        }
    }

    [RelayCommand]
    private void RespondToPrompt(int choice) => CompletePrompt(choice);

    [RelayCommand]
    private void DismissPrompt()
    {
        if (IsNewProjectDialogOpen)
        {
            CancelNewProject();
            return;
        }

        if (ActivePrompt is not null)
        {
            CompletePrompt(ActivePrompt.CancelResult);
        }
    }

    public Task<int> ShowPromptAsync(OverlayPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.Post(() =>
        {
            if (ActivePrompt is not null)
            {
                completion.TrySetResult(request.CancelResult);
                return;
            }

            _promptCompletion = completion;
            ActivePrompt = request;
            IsModalOpen = true;
            _promptCancellation = cancellationToken.Register(
                () => _dispatcher.Post(() => CompletePrompt(request.CancelResult)));
        });

        return completion.Task;
    }

    /// <summary>
    /// Called by project presentation workflows after opening or closing a project.
    /// It refreshes the projection without duplicating lifecycle state in the UI.
    /// </summary>
    public void RefreshNavigation()
    {
        WorkspacePage current = _projects.Navigation.Current;
        bool hasOpenProject = _projects.HasOpenProject;

        foreach (WorkspaceNavigationItem item in NavigationItems.Append(SettingsNavigation))
        {
            item.IsCurrent = item.Page == current;
            item.IsAvailable = IsRouteAvailable(item.Page, hasOpenProject);
        }

        (CurrentPageTitle, CurrentPageDescription, CurrentPageBadge) = GetPagePresentation(current);
        IsHomePage = current == WorkspacePage.Home;
        IsSettingsPage = current == WorkspacePage.Settings;
        IsProjectPage = hasOpenProject && RequiresProject(current);
        IsDesignPage = IsProjectPage && current == WorkspacePage.Design;
        IsPlaceholderPage = IsProjectPage && current != WorkspacePage.Design;
    }

    private WorkspaceNavigationItem CreateNavigationItem(WorkspacePage page, string labelResourceId, string shortcut)
        => new(page, _text[labelResourceId], shortcut, new RelayCommand(() => NavigateTo(page)));

    private void NavigateTo(WorkspacePage destination)
    {
        // Project stages only exist inside an open project; without one the request is
        // explained and the current page stays (the sidebar does not even show them).
        var result = _projects.Navigation.NavigateTo(destination, _projects.HasOpenProject);
        NavigationNotice = result.Succeeded ? null : _text["navigation.requiresProject"];

        RefreshNavigation();
    }

    private static bool IsRouteAvailable(WorkspacePage page, bool hasOpenProject) => hasOpenProject || !RequiresProject(page);

    private static bool RequiresProject(WorkspacePage page)
        => page is not (WorkspacePage.Home or WorkspacePage.Settings);

    private void OnJobStarted(JobStarted job)
    {
        Jobs.Insert(0, new JobDockItem(job.JobId, job.Name)
        {
            Detail = _text["job.started"],
        });
        ShowToast(_text["toast.jobStarted"], job.Name, ToastSeverity.Info, TimeSpan.FromSeconds(3));
    }

    private void OnJobProgress(JobProgressReported update)
    {
        JobDockItem? job = Jobs.FirstOrDefault(item => item.Id == update.JobId);
        if (job is null)
        {
            return;
        }

        job.Detail = update.Progress.Message ?? update.Progress.Stage;
        job.Progress = update.Progress.Fraction;
    }

    private void OnJobFinished(JobFinished job)
    {
        JobDockItem? item = Jobs.FirstOrDefault(candidate => candidate.Id == job.JobId);
        if (item is null)
        {
            return;
        }

        item.State = job.ErrorCode switch
        {
            null => JobPresentationState.Succeeded,
            ApplicationErrorCode.JobCanceled => JobPresentationState.Canceled,
            _ => JobPresentationState.Failed,
        };
        item.Progress = item.State == JobPresentationState.Succeeded ? 1 : item.Progress;
        item.Detail = item.State switch
        {
            JobPresentationState.Succeeded => _text["job.succeeded"],
            JobPresentationState.Canceled => _text["job.canceled"],
            _ => $"{_text["job.failed"]} ({job.ErrorCode})",
        };

        if (item.State == JobPresentationState.Failed)
        {
            IsJobDockExpanded = true;
            ShowToast(_text["toast.jobFailed"], job.Name, ToastSeverity.Error, TimeSpan.FromSeconds(6));
        }
        else if (item.State == JobPresentationState.Succeeded)
        {
            ShowToast(_text["toast.jobCompleted"], job.Name, ToastSeverity.Success, TimeSpan.FromSeconds(3));
        }
        else
        {
            ShowToast(_text["toast.jobCanceled"], job.Name, ToastSeverity.Info, TimeSpan.FromSeconds(4));
        }
    }

    private void OnProjectOpened(ProjectOpened project)
    {
        RefreshProjectState();
        RefreshHistoryState();
        _ = Recent.RefreshAsync();
        _ = Preview.RefreshAsync();
        ProjectActionStatus = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _text["project.status.opened"],
            project.ProjectName);
        RefreshNavigation();
    }

    private void OnProjectSaved(ProjectSaved project)
    {
        RefreshProjectState();
        _ = Recent.RefreshAsync();
        ProjectActionStatus = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _text["project.status.saved"],
            project.ProjectName);
    }

    private void OnProjectClosed(ProjectClosed project)
    {
        RefreshProjectState();
        RefreshHistoryState();
        Preview.Clear();
        _compositions.Clear();
        ProjectActionStatus = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _text["project.status.closed"],
            project.ProjectName);
        HomeActionStatus = ProjectActionStatus;
        RefreshNavigation();
    }

    private void OnProjectDirtyChanged(ProjectDirtyChanged change) => RefreshProjectState();

    private async Task<bool> OpenProjectAsync(string path)
    {
        if (IsProjectOperationInProgress || IsAnyDialogOpen)
        {
            return false;
        }

        IsProjectOperationInProgress = true;
        ProjectActionError = null;
        try
        {
            ApplicationResult<ProjectWorkspace> result = await Task.Run(() => _projects.OpenProjectAsync(path));
            if (!result.Succeeded)
            {
                ProjectActionError = GetProjectOpenError(result.Error);
            }

            return result.Succeeded;
        }
        finally
        {
            IsProjectOperationInProgress = false;
            RefreshProjectState();
            RefreshNavigation();
        }
    }

    private async Task PersistProjectAsync(
        Func<Task<ApplicationResult<ProjectWorkspace>>> persist,
        bool isSaveAs)
    {
        IsProjectOperationInProgress = true;
        ProjectActionError = null;
        try
        {
            ApplicationResult<ProjectWorkspace> result = await Task.Run(persist);
            if (!result.Succeeded)
            {
                ProjectActionError = GetProjectSaveError(result.Error);
                return;
            }

            ProjectActionStatus = isSaveAs
                ? _text["project.status.savedAs"]
                : string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    _text["project.status.saved"],
                    result.Value.Identity.Name);
        }
        finally
        {
            IsProjectOperationInProgress = false;
            RefreshProjectState();
        }
    }

    private void RefreshProjectState()
    {
        ProjectWorkspace? project = _projects.Current;
        HasOpenProject = project is not null;
        IsProjectDirty = _projects.Dirty.IsDirty;
        ProjectName = project?.Identity.Name ?? string.Empty;
        ProjectFilePath = _projects.ProjectFilePath ?? string.Empty;
        ProjectFamilyVariant = project is null
            ? "cd"
            : DiscImageStudio.Application.Projects.RecentProjectsService.ToFamilyToken(project.Media.Family);
        RefreshNavigation();
        _ = RefreshMissingAssetsAsync();
    }

    /// <summary>
    /// Counts missing assets off the UI thread. A project with missing assets still opens; the
    /// toolbar only points the user to the recovery panel in project settings.
    /// </summary>
    private async Task RefreshMissingAssetsAsync()
    {
        ProjectWorkspace? project = _projects.Current;
        string? path = _projects.ProjectFilePath;
        if (project is null)
        {
            MissingAssetCount = 0;
            return;
        }

        int missing = await Task.Run(() => _assets.Inspect(project, path).Count(asset => !asset.Exists));
        if (ReferenceEquals(project, _projects.Current))
        {
            MissingAssetCount = missing;
        }
    }

    private string GetSuggestedProjectDirectory()
    {
        string? currentDirectory = string.IsNullOrWhiteSpace(_projects.ProjectFilePath)
            ? null
            : Path.GetDirectoryName(_projects.ProjectFilePath);
        return !string.IsNullOrWhiteSpace(currentDirectory)
            ? currentDirectory
            : string.IsNullOrWhiteSpace(_settings.DefaultProjectDirectory)
                ? _paths.ProjectsDirectory
                : _settings.DefaultProjectDirectory;
    }

    private string GetSuggestedProjectFileName()
    {
        string? currentFileName = string.IsNullOrWhiteSpace(_projects.ProjectFilePath)
            ? null
            : Path.GetFileName(_projects.ProjectFilePath);
        return !string.IsNullOrWhiteSpace(currentFileName)
            ? currentFileName
            : BuildProjectFileName(ProjectName);
    }

    private static string EnsureProjectFileExtension(string path)
        => string.Equals(Path.GetExtension(path), ProjectJsonStore.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ProjectJsonStore.FileExtension);

    private string GetProjectOpenError(ApplicationError? error)
    {
        if (error?.Code == ApplicationErrorCode.UserCanceled)
        {
            return _text["project.error.operationCanceled"];
        }

        if (error?.Code == ApplicationErrorCode.ProjectSchemaUnsupported)
        {
            return _text["openProject.error.unsupportedSchema"];
        }

        return error?.Exception switch
        {
            UnauthorizedAccessException => _text["openProject.error.permissionDenied"],
            FileNotFoundException or DirectoryNotFoundException => _text["openProject.error.notFound"],
            InvalidDataException => _text["openProject.error.invalidFile"],
            _ => _text["openProject.error.readFailed"],
        };
    }

    private string GetProjectSaveError(ApplicationError? error)
        => error?.Exception switch
        {
            UnauthorizedAccessException => _text["saveProject.error.permissionDenied"],
            InvalidDataException or ArgumentException => _text["saveProject.error.invalidProject"],
            _ => _text["saveProject.error.writeFailed"],
        };

    private void ShowToast(string title, string message, ToastSeverity severity, TimeSpan lifetime)
    {
        ToastNotification toast = new(Guid.NewGuid(), title, message, severity, lifetime);
        Toasts.Add(toast);
        _ = RemoveToastWhenExpiredAsync(toast);
    }

    private async Task RemoveToastWhenExpiredAsync(ToastNotification toast)
    {
        await Task.Delay(toast.Lifetime);
        _dispatcher.Post(() => Toasts.Remove(toast));
    }

    private void CompletePrompt(int choice)
    {
        if (_promptCompletion is null)
        {
            return;
        }

        TaskCompletionSource<int> completion = _promptCompletion;
        _promptCompletion = null;
        _promptCancellation.Dispose();
        ActivePrompt = null;
        IsModalOpen = false;
        completion.TrySetResult(choice);
    }

    private void RefreshProjectPresets()
    {
        OpticalDiscFamily family = SelectedHomeTemplate?.Family ?? OpticalDiscFamily.CompactDisc;
        string? selectedId = SelectedProjectPreset?.Id;
        NewProjectPresets.Clear();
        foreach (NewProjectPresetOption preset in ProjectPresets.Where(preset => preset.Family == family))
        {
            NewProjectPresets.Add(preset);
        }

        foreach (ProjectPresetDefinition preset in _userPresets.Where(preset => preset.Family == family))
        {
            NewProjectPresets.Add(NewProjectPresetOption.FromUserPreset(preset));
        }

        SelectedProjectPreset = NewProjectPresets.FirstOrDefault(preset => preset.Id == selectedId)
            ?? NewProjectPresets.FirstOrDefault();
    }

    private bool TryBuildNewProjectRequest(out CreateProjectRequest request)
    {
        request = null!;
        string name = NewProjectName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NewProjectError = _text["newProject.error.nameRequired"];
            return false;
        }

        string directory = NewProjectDirectory.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            NewProjectError = _text["newProject.error.directoryRequired"];
            return false;
        }

        NewProjectPresetOption? preset = SelectedProjectPreset;
        if (preset is null)
        {
            NewProjectError = _text["newProject.error.presetRequired"];
            return false;
        }

        try
        {
            string projectPath = _fileSystem.GetFullPath(Path.Combine(directory, BuildProjectFileName(name)));
            if (_fileSystem.FileExists(projectPath))
            {
                NewProjectError = _text["newProject.error.fileExists"];
                return false;
            }

            string outputPath = Path.ChangeExtension(projectPath, ".iso");
            request = new CreateProjectRequest(
                projectPath,
                name,
                preset.Media,
                new DeliveryState(
                    outputPath,
                    dataDirectory: string.Empty,
                    NewProjectVolumeLabel.Trim(),
                    NewProjectFilesAtOuterRing),
                new DesignState(),
                new ToneState(ToneProfile.Photo));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            NewProjectError = _text["newProject.error.invalidPath"];
            return false;
        }
    }

    private string CreateProjectError(ApplicationErrorCode? code)
        => code switch
        {
            ApplicationErrorCode.UserCanceled => _text["newProject.error.canceled"],
            ApplicationErrorCode.ProjectWriteFailed => _text["newProject.error.writeFailed"],
            _ => _text["newProject.error.createFailed"],
        };

    private void UpdateNewProjectFilePathPreview()
    {
        string name = NewProjectName.Trim();
        string directory = NewProjectDirectory.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(directory))
        {
            NewProjectFilePathPreview = string.Empty;
            return;
        }

        try
        {
            NewProjectFilePathPreview = _fileSystem.GetFullPath(Path.Combine(directory, BuildProjectFileName(name)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            NewProjectFilePathPreview = string.Empty;
        }
    }

    private static string BuildProjectFileName(string name)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string stem = new string(name.Trim().Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .Trim('.');
        return $"{(string.IsNullOrWhiteSpace(stem) ? "disc-project" : stem)}{ProjectJsonStore.FileExtension}";
    }

    private static IReadOnlyList<NewProjectPresetOption> CreateProjectPresets(ITextCatalog text)
    {
        DiscPresetJsonDocument catalog = DiscPresetJsonStore.LoadBuiltIn();
        CdDiscPresetDefinition cd = catalog.CdPresets.FirstOrDefault(preset => preset.Id == "cd-80")
            ?? throw new InvalidOperationException("The standard CD preset is missing from the catalog.");
        DvdDiscPresetDefinition dvd = catalog.DvdPresets.FirstOrDefault(preset => preset.Id == "dvd-5-120mm")
            ?? throw new InvalidOperationException("The standard DVD preset is missing from the catalog.");
        BluRayDiscPresetDefinition bluRay = (catalog.BluRayPresets ?? [])
            .FirstOrDefault(preset => preset.Id == "bd-r-sl-25gb")
            ?? throw new InvalidOperationException("The standard Blu-ray preset is missing from the catalog.");

        return
        [
            new NewProjectPresetOption(
                cd.Id,
                OpticalDiscFamily.CompactDisc,
                text["newProject.preset.cd.title"],
                text["newProject.preset.cd.description"],
                new CdState(cd.InnerRadiusMm, cd.OuterRadiusMm, cd.Sectors, writeCue: true, interleave: true)),
            new NewProjectPresetOption(
                dvd.Id,
                OpticalDiscFamily.Dvd,
                text["newProject.preset.dvd.title"],
                text["newProject.preset.dvd.description"],
                new DvdState(
                    dvd.InnerRadiusMm,
                    dvd.OuterRadiusMm,
                    dvd.TotalSectors,
                    dvd.PitchLinear,
                    dvd.PitchQuadratic,
                    dvd.PitchCubic)),
            new NewProjectPresetOption(
                bluRay.Id,
                OpticalDiscFamily.BluRay,
                text["newProject.preset.bd.title"],
                text["newProject.preset.bd.description"],
                new BluRayState(
                    bluRay.InnerRadiusMm,
                    bluRay.OuterRadiusMm,
                    bluRay.TotalSectors,
                    bluRay.InvertMapping)),
        ];
    }

    private (string Title, string Description, string Badge) GetPagePresentation(WorkspacePage page)
        => page switch
        {
            WorkspacePage.Home => (
                _text["home.placeholder.title"],
                _text["home.placeholder.description"],
                string.Empty),
            WorkspacePage.Settings => (
                _text["settings.placeholder.title"],
                _text["settings.placeholder.description"],
                string.Empty),
            WorkspacePage.Design => (
                _text["design.page.title"],
                _text["design.page.description"],
                ProjectFamilyBadge),
            _ => (
                _text[$"navigation.{page.ToString().ToLowerInvariant()}"],
                _text["workspace.placeholder.description"],
                ProjectFamilyBadge),
        };

    /// <summary>Project pages carry the disc family as their badge (CD, DVD or BD).</summary>
    private string ProjectFamilyBadge => ProjectFamilyVariant.ToUpperInvariant();

    private string BuildDiagnostics()
        => $"{Title}\nVersion: {ProductVersion}\nCommit: {Commit}\n.NET: {DotnetRuntime}\n"
           + $"Avalonia: {AvaloniaVersion}\nOS: {OperatingSystem}\nConfig: {ConfigDirectory}\n"
           + $"Data: {DataDirectory}\nCache: {CacheDirectory}\nLogs: {LogsDirectory}";
}
