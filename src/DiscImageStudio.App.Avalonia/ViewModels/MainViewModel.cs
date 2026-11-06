using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Diagnostics;
using DiscImageStudio.App.Avalonia.Feedback;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Navigation;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;

namespace DiscImageStudio.App.Avalonia.ViewModels;

public partial class MainViewModel : ViewModelBase, IOverlayPromptPresenter
{
    private readonly IAppPaths _paths;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLauncher _launcher;
    private readonly ITextCatalog _text;
    private readonly AboutInfo _about;
    private readonly ProjectLifecycleService _projects;
    private readonly JobCoordinator _jobs;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<IDisposable> _eventSubscriptions = [];
    private TaskCompletionSource<int>? _promptCompletion;
    private CancellationTokenRegistration _promptCancellation;

    public MainViewModel(
        IAppPaths paths,
        IClipboardService clipboard,
        IExternalLauncher launcher,
        ITextCatalog text,
        AboutInfo about,
        ProjectLifecycleService projects,
        JobCoordinator jobs,
        IUiDispatcher dispatcher,
        IApplicationEventStream applicationEvents)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _about = about ?? throw new ArgumentNullException(nameof(about));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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
    }

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
    public WorkspaceNavigationItem HomeNavigation { get; }
    public WorkspaceNavigationItem DesignNavigation { get; }
    public WorkspaceNavigationItem PreviewNavigation { get; }
    public WorkspaceNavigationItem ExportNavigation { get; }
    public WorkspaceNavigationItem BurnNavigation { get; }
    public WorkspaceNavigationItem SettingsNavigation { get; }
    public IReadOnlyList<WorkspaceNavigationItem> NavigationItems { get; }
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
        IsPlaceholderPage = !IsHomePage;
    }

    private WorkspaceNavigationItem CreateNavigationItem(WorkspacePage page, string labelResourceId, string shortcut)
        => new(page, _text[labelResourceId], shortcut, new RelayCommand(() => NavigateTo(page)));

    private void NavigateTo(WorkspacePage destination)
    {
        var result = _projects.Navigation.NavigateTo(
            destination,
            _projects.HasOpenProject,
            allowProjectlessNavigation: true);
        if (!result.Succeeded)
        {
            _ = _projects.Navigation.NavigateTo(WorkspacePage.Home, hasOpenProject: false);
            NavigationNotice = _text["navigation.requiresProject"];
        }
        else
        {
            NavigationNotice = !_projects.HasOpenProject && RequiresProject(destination)
                ? _text["navigation.placeholderRequiresProject"]
                : null;
        }

        RefreshNavigation();
    }

    private static bool IsRouteAvailable(WorkspacePage page, bool hasOpenProject) => true;

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

    private (string Title, string Description, string Badge) GetPagePresentation(WorkspacePage page)
        => page switch
        {
            WorkspacePage.Home => (
                _text["home.placeholder.title"],
                _text["home.placeholder.description"],
                "HOME"),
            WorkspacePage.Settings => (
                _text["settings.placeholder.title"],
                _text["settings.placeholder.description"],
                "SETTINGS"),
            _ => (
                _text["workspace.placeholder.title"],
                _text["workspace.placeholder.description"],
                page.ToString().ToUpperInvariant()),
        };

    private string BuildDiagnostics()
        => $"{Title}\nVersion: {ProductVersion}\nCommit: {Commit}\n.NET: {DotnetRuntime}\n"
           + $"Avalonia: {AvaloniaVersion}\nOS: {OperatingSystem}\nConfig: {ConfigDirectory}\n"
           + $"Data: {DataDirectory}\nCache: {CacheDirectory}\nLogs: {LogsDirectory}";
}
