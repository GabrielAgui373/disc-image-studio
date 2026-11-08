using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.ViewModels.Export;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.Generation;
using DiscImageStudio.Preview;

namespace DiscImageStudio.App.Avalonia.ViewModels.Burn;

public enum BurnSourceKind
{
    Project,
    CalibrationTarget,
}

/// <summary>A file the page can burn: the project's published image or its calibration target.</summary>
public sealed record BurnSourceOption(
    BurnSourceKind Kind,
    string Title,
    string Path,
    OpticalBurnMediaKind? Mode,
    long Bytes,
    DateTime LastWriteUtc,
    string? Sha256,
    string? Problem);

public sealed record BurnDeviceItem(OpticalDeviceStatus Status, string Title, string Detail);

public sealed record BurnSpeedOption(string Label, OpticalWriteSpeed? Speed);

public enum BurnPhase
{
    Review,
    Confirm,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>
/// The Burn page (Task 13.2). Lists the recorders and their discs through the portable
/// <see cref="IOpticalDeviceService"/>, shows per mode why a burn is or is not possible, and
/// offers export as the alternative while no backend can write. A burn is confirmed on one
/// screen that names the mode, recorder, disc, capacity, speed, source and that it cannot be
/// undone; the confirmation is tied to a fingerprint of the disc and the file, so a refresh that
/// finds another disc (or a changed file) drops it, and the backend checks the disc identity again
/// right before writing. Refreshing never touches a running burn.
/// </summary>
public sealed partial class BurnWorkspaceViewModel : ObservableObject
{
    private readonly IOpticalDeviceService _devices;
    private readonly IOpticalBurner _burner;
    private readonly ProjectLifecycleService _projects;
    private readonly ITextCatalog _text;
    private JobCoordinator? _jobs;
    private Guid? _jobId;
    private bool _isActive;
    private bool _stale = true;
    private string? _confirmedFingerprint;
    private CancellationTokenSource? _refreshing;

    public BurnWorkspaceViewModel(IOpticalDeviceService devices, IOpticalBurner burner, ProjectLifecycleService projects, ITextCatalog text)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _burner = burner ?? throw new ArgumentNullException(nameof(burner));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Problems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProblems));
    }

    /// <summary>Raised by "Export instead".</summary>
    public event Action? GoToExportRequested;

    // ---- Labels ----

    public string SourceTitle => _text["burn.source.title"];

    public string DevicesTitle => _text["burn.devices.title"];

    public string RefreshLabel => _text["burn.refresh"];

    public string ModesTitle => _text["burn.modes.title"];

    public string SpeedLabel => _text["burn.speed"];

    public string ProblemsTitle => _text["export.problems.title"];

    public string ExportInsteadLabel => _text["burn.exportInstead"];

    public string ReviewLabel => _text["burn.review"];

    public string ConfirmTitle => _text["burn.confirm.title"];

    public string IrreversibleText => _text["burn.confirm.irreversible"];

    public string BluRayWarningText => _text["burn.confirm.bluray"];

    public string AcknowledgeLabel => _text["burn.confirm.acknowledge"];

    public string BurnLabel => _text["burn.burn"];

    public string BackLabel => _text["burn.confirm.back"];

    public string CancelRunLabel => _text["export.run.cancel"];

    public string DoneLabel => _text["export.result.done"];

    public string BackendText => string.Format(CultureInfo.CurrentUICulture, _text["burn.backend"], _devices.BackendName);

    // ---- State ----

    public ObservableCollection<BurnSourceOption> Sources { get; } = [];

    public ObservableCollection<BurnDeviceItem> Devices { get; } = [];

    public ObservableCollection<ExportSummaryRow> ModeRows { get; } = [];

    public ObservableCollection<BurnSpeedOption> Speeds { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<ExportSummaryRow> ConfirmRows { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool IsBackendAvailable => _devices.Availability.IsAvailable;

    public string BackendUnavailableText => IsBackendAvailable ? string.Empty : _text["burn.backend.unavailable"];

    [ObservableProperty]
    public partial BurnSourceOption? SelectedSource { get; set; }

    [ObservableProperty]
    public partial BurnDeviceItem? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial BurnSpeedOption? SelectedSpeed { get; set; }

    [ObservableProperty]
    public partial string SourceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Notice { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewPhase), nameof(IsConfirming), nameof(IsRunning), nameof(HasOutcome), nameof(IsSucceeded))]
    [NotifyCanExecuteChangedFor(nameof(ReviewCommand), nameof(BurnCommand))]
    public partial BurnPhase Phase { get; private set; } = BurnPhase.Review;

    public bool IsReviewPhase => Phase == BurnPhase.Review;

    public bool IsConfirming => Phase == BurnPhase.Confirm;

    public bool IsRunning => Phase == BurnPhase.Running;

    public bool HasOutcome => Phase is BurnPhase.Succeeded or BurnPhase.Failed or BurnPhase.Canceled;

    public bool IsSucceeded => Phase == BurnPhase.Succeeded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BurnCommand))]
    public partial bool Acknowledged { get; set; }

    [ObservableProperty]
    public partial bool ShowBluRayWarning { get; private set; }

    [ObservableProperty]
    public partial string StageText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent), nameof(IsProgressKnown))]
    public partial double? ProgressFraction { get; private set; }

    public double ProgressPercent => (ProgressFraction ?? 0) * 100;

    public bool IsProgressKnown => ProgressFraction is not null;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OutcomeTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OutcomeText { get; private set; } = string.Empty;

    /// <summary>Called once by the shell.</summary>
    public void AttachJobs(JobCoordinator jobs)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        ReviewCommand.NotifyCanExecuteChanged();
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && _stale)
        {
            _stale = false;
            _ = RefreshAsync();
        }
    }

    /// <summary>The project changed (output path, linked session): rebuild the sources.</summary>
    public void RequestRefresh()
    {
        if (!_isActive)
        {
            _stale = true;
            return;
        }

        RebuildSources();
        Evaluate();
    }

    public void Clear()
    {
        _refreshing?.Cancel();
        _stale = true;
        _confirmedFingerprint = null;
        Sources.Clear();
        Devices.Clear();
        ModeRows.Clear();
        Speeds.Clear();
        Problems.Clear();
        ConfirmRows.Clear();
        SelectedSource = null;
        SelectedDevice = null;
        Notice = string.Empty;
        if (Phase != BurnPhase.Running)
        {
            Phase = BurnPhase.Review;
        }
    }

    // ---- Commands ----

    /// <summary>Reads the recorders again. A running burn is not touched; it keeps its own request.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _refreshing?.Cancel();
        CancellationTokenSource cancellation = new();
        _refreshing = cancellation;
        IsRefreshing = true;
        try
        {
            RebuildSources();
            IReadOnlyList<OpticalDeviceStatus> devices = IsBackendAvailable
                ? await _devices.GetDevicesAsync(cancellation.Token)
                : [];
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            string? keep = SelectedDevice?.Status.Device.Id;
            Devices.Clear();
            foreach (OpticalDeviceStatus status in devices)
            {
                Devices.Add(new BurnDeviceItem(status, DeviceTitle(status), MediaText(status)));
            }

            // Keep the user's recorder; otherwise prefer one that can write the selected image now.
            SelectedDevice = Devices.FirstOrDefault(item => item.Status.Device.Id == keep)
                             ?? Devices.FirstOrDefault(item => SelectedSource?.Mode is { } mode && item.Status.Mode(mode).IsAvailable)
                             ?? Devices.FirstOrDefault();
            await LoadSpeedsAsync();
            Evaluate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (OpticalBurnException exception)
        {
            Problems.Clear();
            Problems.Add(ErrorText(exception.Code));
        }
        finally
        {
            if (ReferenceEquals(_refreshing, cancellation))
            {
                IsRefreshing = false;
            }
        }
    }

    [RelayCommand]
    private void ExportInstead() => GoToExportRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanReview))]
    private void Review()
    {
        if (!IsReady || SelectedDevice is not { } device || SelectedSource is not { } source || source.Mode is not { } mode)
        {
            return;
        }

        OpticalMediaState media = device.Status.Media!;
        ConfirmRows.Clear();
        ConfirmRows.Add(new(_text["burn.row.mode"], ModeText(mode)));
        ConfirmRows.Add(new(_text["burn.row.device"], DeviceTitle(device.Status)));
        ConfirmRows.Add(new(_text["burn.row.disc"], MediaText(device.Status)));
        ConfirmRows.Add(new(_text["burn.row.capacity"], string.Format(
            CultureInfo.CurrentUICulture,
            _text["burn.value.capacity"],
            ByteSize.Format(source.Bytes),
            ByteSize.Format(media.FreeBytes(mode)),
            (double)source.Bytes / media.FreeBytes(mode) * 100)));
        ConfirmRows.Add(new(_text["burn.row.speed"], SelectedSpeed?.Label ?? _text["burn.speed.max"]));
        ConfirmRows.Add(new(_text["burn.row.source"], string.Format(
            CultureInfo.CurrentUICulture,
            _text["burn.value.source"],
            System.IO.Path.GetFileName(source.Path),
            source.Sha256 is { Length: >= 16 } hash ? hash[..16] : _text["burn.value.noManifest"])));
        ShowBluRayWarning = mode == OpticalBurnMediaKind.BluRayData;
        Acknowledged = false;
        _confirmedFingerprint = Fingerprint();
        Notice = string.Empty;
        Phase = BurnPhase.Confirm;
    }

    private bool CanReview() => _jobs is not null && IsReady && Phase is BurnPhase.Review or BurnPhase.Succeeded or BurnPhase.Failed or BurnPhase.Canceled;

    [RelayCommand]
    private void BackToReview()
    {
        _confirmedFingerprint = null;
        Phase = BurnPhase.Review;
    }

    [RelayCommand(CanExecute = nameof(CanBurn))]
    private async Task BurnAsync()
    {
        if (_jobs is not { } jobs || SelectedDevice is not { } device || SelectedSource is not { } source || source.Mode is not { } mode)
        {
            return;
        }

        // The confirmation holds only for what was reviewed.
        if (Fingerprint() != _confirmedFingerprint)
        {
            InvalidateConfirmation();
            return;
        }

        OpticalBurnSource burnSource;
        try
        {
            burnSource = OpticalBurnSources.FromArtifact(source.Path, mode);
        }
        catch (OpticalBurnException exception)
        {
            ShowFailure(exception);
            return;
        }

        OpticalBurnRequest request = burnSource.ToRequest(device.Status.Device.Id, SelectedSpeed?.Speed, device.Status.Media?.Identity);
        _confirmedFingerprint = null;
        Phase = BurnPhase.Running;
        StageText = _text["burn.stage.prepare"];
        ProgressFraction = null;
        ProgressText = string.Empty;
        OpticalBurnResult? result = null;
        ApplicationResult<JobCompletion> job = await jobs.StartJobAsync(
            new JobRequest(string.Format(CultureInfo.CurrentUICulture, _text["burn.job.name"], System.IO.Path.GetFileName(source.Path))),
            async context =>
            {
                _jobId = context.JobId;
                Progress<OpticalBurnProgress> progress = new(update => ShowProgress(update, context.Progress));
                result = await _burner.BurnAsync(request, progress, context.CancellationToken);
            });
        _jobId = null;
        if (job.Succeeded && result is { } done)
        {
            OutcomeTitle = _text["burn.result.succeededTitle"];
            OutcomeText = string.Format(
                CultureInfo.CurrentUICulture, _text["burn.result.succeededText"], ByteSize.Format(done.BytesWritten), Clock(done.Elapsed), device.Title);
            Phase = BurnPhase.Succeeded;
        }
        else if (job.Error?.Exception is OpticalBurnCleanupException)
        {
            OutcomeTitle = _text["export.result.canceledTitle"];
            OutcomeText = _text["burn.result.cleanupFailed"];
            Phase = BurnPhase.Canceled;
        }
        else if (job.Error?.Code == ApplicationErrorCode.JobCanceled)
        {
            OutcomeTitle = _text["export.result.canceledTitle"];
            OutcomeText = _text["burn.result.canceledText"];
            Phase = BurnPhase.Canceled;
        }
        else if (job.Error?.Exception is OpticalBurnException failure)
        {
            ShowFailure(failure);
        }
        else
        {
            OutcomeTitle = _text["burn.result.failedTitle"];
            OutcomeText = job.Error?.Code == ApplicationErrorCode.JobConflict
                ? _text["calibration.failure.busy"]
                : string.Format(CultureInfo.CurrentUICulture, _text["export.failure.unexpected"], job.Error?.Detail ?? string.Empty);
            Phase = BurnPhase.Failed;
        }

        // The disc changed state (written, or touched by a failed burn): read it again.
        await RefreshAsync();
    }

    private bool CanBurn() => Phase == BurnPhase.Confirm && Acknowledged;

    [RelayCommand]
    private void CancelRun()
    {
        if (_jobs is not null && _jobId is { } id)
        {
            _jobs.CancelJob(id);
            StageText = _text["export.stage.canceling"];
        }
    }

    [RelayCommand]
    private void Done() => Phase = BurnPhase.Review;

    partial void OnSelectedSourceChanged(BurnSourceOption? value)
    {
        _ = LoadSpeedsAsync();
        Evaluate();
    }

    partial void OnSelectedDeviceChanged(BurnDeviceItem? value)
    {
        _ = LoadSpeedsAsync();
        Evaluate();
    }

    partial void OnSelectedSpeedChanged(BurnSpeedOption? value) => Evaluate();

    partial void OnIsReadyChanged(bool value) => ReviewCommand.NotifyCanExecuteChanged();

    // ---- Evaluation ----

    private void RebuildSources()
    {
        string? keep = SelectedSource?.Path;
        Sources.Clear();
        if (_projects.Current is not { } workspace)
        {
            return;
        }

        if (ArtifactPreviewService.ArtifactPath(workspace, _projects.ProjectFilePath) is { } output)
        {
            Sources.Add(Describe(BurnSourceKind.Project, _text["burn.source.project"], output, workspace.Media.Family));
        }

        if (workspace.Identity.CalibrationSessionPath is { Length: > 0 } sessionPath && CalibrationArtifact(sessionPath) is { } target)
        {
            Sources.Add(Describe(BurnSourceKind.CalibrationTarget, _text["burn.source.calibration"], target.Path, target.Family));
        }

        SelectedSource = Sources.FirstOrDefault(source => source.Path == keep) ?? Sources.FirstOrDefault();
    }

    private BurnSourceOption Describe(BurnSourceKind kind, string title, string path, OpticalDiscFamily family)
    {
        string token = family switch
        {
            OpticalDiscFamily.CompactDisc => "cd",
            OpticalDiscFamily.BluRay => "bd",
            _ => "dvd",
        };
        OpticalBurnMediaKind? mode = OpticalBurnSources.ModeFor(token, path);
        FileInfo file = new(path);
        if (!file.Exists)
        {
            return new(kind, title, path, mode, 0, default, null, _text["burn.problem.sourceMissing"]);
        }

        ArtifactManifest? manifest = ArtifactManifest.TryLoad(path);
        string? problem = mode is null
            ? _text["burn.problem.sourceFormat"]
            : manifest is { } m && m.Artifact.Bytes != file.Length ? _text["burn.problem.sourceChanged"] : null;
        return new(kind, title, path, mode, file.Length, file.LastWriteTimeUtc, manifest?.Artifact.Sha256, problem);
    }

    /// <summary>The target published beside a calibration session, if it is there.</summary>
    private static (string Path, OpticalDiscFamily Family)? CalibrationArtifact(string sessionPath)
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(sessionPath) ?? string.Empty;
            string name = System.IO.Path.GetFileName(sessionPath);
            string stem = name.EndsWith(CalibrationSessionStore.Extension, StringComparison.OrdinalIgnoreCase)
                ? name[..^CalibrationSessionStore.Extension.Length]
                : System.IO.Path.GetFileNameWithoutExtension(name);
            foreach (string extension in new[] { ".iso", ".wav", ".raw" })
            {
                string candidate = System.IO.Path.Combine(directory, stem + extension);
                if (File.Exists(candidate) && ArtifactManifest.TryLoad(candidate) is { } manifest)
                {
                    OpticalDiscFamily family = manifest.Family switch
                    {
                        nameof(OpticalDiscFamily.CompactDisc) => OpticalDiscFamily.CompactDisc,
                        nameof(OpticalDiscFamily.BluRay) => OpticalDiscFamily.BluRay,
                        _ => OpticalDiscFamily.Dvd,
                    };
                    return (candidate, family);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    private async Task LoadSpeedsAsync()
    {
        string? keep = SelectedSpeed?.Label;
        Speeds.Clear();
        Speeds.Add(new BurnSpeedOption(_text["burn.speed.max"], null));
        if (SelectedDevice is { } device && SelectedSource?.Mode is { } mode && IsBackendAvailable)
        {
            try
            {
                foreach (OpticalWriteSpeed speed in await _burner.GetSupportedWriteSpeedsAsync(device.Status.Device.Id, mode))
                {
                    Speeds.Add(new BurnSpeedOption(
                        string.Format(CultureInfo.CurrentUICulture, _text["burn.speed.value"], speed.GetMultiplier(mode), speed.GetMegabytesPerSecond(mode)),
                        speed));
                }
            }
            catch (OpticalBurnException)
            {
            }
        }

        SelectedSpeed = Speeds.FirstOrDefault(speed => speed.Label == keep) ?? Speeds[0];
    }

    /// <summary>What blocks a burn, per mode capability, disc and source; and whether a review is possible.</summary>
    private void Evaluate()
    {
        Problems.Clear();
        ModeRows.Clear();
        BurnSourceOption? source = SelectedSource;
        SourceText = source is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentUICulture, _text["burn.source.text"], source.Path, source.Bytes > 0 ? ByteSize.Format(source.Bytes) : "—", source.Mode is { } m ? ModeText(m) : "—");
        if (!IsBackendAvailable)
        {
            Problems.Add(_text["burn.backend.unavailable"]);
        }

        if (source is null)
        {
            Problems.Add(_text["burn.problem.noSource"]);
        }
        else if (source.Problem is { } problem)
        {
            Problems.Add(problem);
        }

        if (IsBackendAvailable && SelectedDevice is null)
        {
            Problems.Add(_text["burn.problem.noDevice"]);
        }

        if (SelectedDevice is { } device)
        {
            foreach (OpticalModeCapability capability in device.Status.Modes)
            {
                ModeRows.Add(new(ModeText(capability.Mode), capability.IsAvailable ? _text["burn.mode.available"] : ReasonText(capability.Reason)));
            }

            if (source?.Mode is { } mode)
            {
                OpticalModeCapability capability = device.Status.Mode(mode);
                if (!capability.IsAvailable)
                {
                    Problems.Add(string.Format(CultureInfo.CurrentUICulture, _text["burn.problem.mode"], ModeText(mode), ReasonText(capability.Reason)));
                    if (capability.Reason == OpticalBurnUnavailableReason.PermissionDenied)
                    {
                        Problems.Add(_text["burn.problem.permissionHelp"]);
                    }
                }
                else if (device.Status.Media is { } media && source.Bytes > media.FreeBytes(mode))
                {
                    Problems.Add(string.Format(
                        CultureInfo.CurrentUICulture, _text["burn.problem.capacity"], ByteSize.Format(source.Bytes), ByteSize.Format(media.FreeBytes(mode))));
                }
            }
        }

        IsReady = Problems.Count == 0 && source is not null && SelectedDevice is not null;
        StatusText = IsReady ? _text["burn.status.ready"] : _text["burn.status.blocked"];

        // A confirmation is only valid for the disc and the file that were reviewed.
        if (Phase == BurnPhase.Confirm && Fingerprint() != _confirmedFingerprint)
        {
            InvalidateConfirmation();
        }
    }

    private void InvalidateConfirmation()
    {
        _confirmedFingerprint = null;
        Acknowledged = false;
        Phase = BurnPhase.Review;
        Notice = _text["burn.confirm.invalidated"];
    }

    /// <summary>Everything the user confirms: recorder, disc identity and space, mode, speed and the file.</summary>
    private string? Fingerprint()
    {
        if (SelectedDevice is not { } device || SelectedSource is not { } source)
        {
            return null;
        }

        OpticalMediaState? media = device.Status.Media;
        return string.Join(
            '|',
            device.Status.Device.Id,
            device.Status.Access,
            media?.Identity,
            media?.Profile,
            media?.IsBlank,
            media?.FreeSectors,
            source.Mode,
            source.Path,
            source.Bytes,
            source.LastWriteUtc.Ticks,
            source.Sha256,
            SelectedSpeed?.Speed?.SectorsPerSecond);
    }

    private void ShowProgress(OpticalBurnProgress update, IProgress<JobProgress>? job)
    {
        OpticalBurnStage stage = OpticalBurnStages.Parse(update.Stage);
        StageText = _text[$"burn.stage.{stage.ToString().ToLowerInvariant()}"];
        bool writing = stage == OpticalBurnStage.Write && update.TotalBytes > 0;
        ProgressFraction = writing ? update.Fraction : null;
        ProgressText = writing
            ? string.Format(
                CultureInfo.CurrentUICulture,
                _text["burn.progress"],
                ByteSize.Format(update.CompletedBytes),
                ByteSize.Format(update.TotalBytes),
                ByteSize.Format((long)(update.BytesPerSecond ?? 0)))
            : string.Empty;
        job?.Report(new JobProgress(stage.ToString(), $"{StageText} {ProgressText}".Trim(), ProgressFraction));
    }

    private void ShowFailure(OpticalBurnException exception)
    {
        OutcomeTitle = _text["burn.result.failedTitle"];
        OutcomeText = ErrorText(exception.Code) + (exception.CleanupFailed ? " " + _text["burn.result.cleanupFailed"] : string.Empty);
        Phase = BurnPhase.Failed;
    }

    // ---- Texts ----

    private string DeviceTitle(OpticalDeviceStatus status)
    {
        string? model = status.Model == status.Device.DisplayName ? null : status.Model;
        string details = string.Join(" ", new[] { status.Vendor, model, status.Firmware }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return details.Length == 0 ? status.Device.DisplayName : $"{status.Device.DisplayName} · {details}";
    }

    private string MediaText(OpticalDeviceStatus status)
    {
        if (status.Access != OpticalDeviceAccess.Ready)
        {
            return _text[$"burn.access.{status.Access.ToString().ToLowerInvariant()}"];
        }

        if (status.Media is not { } media)
        {
            return _text["burn.media.none"];
        }

        string state = media.IsBlank ? _text["burn.media.blank"] : _text["burn.media.used"];
        return string.Format(
            CultureInfo.CurrentUICulture,
            _text["burn.media.text"],
            _text[$"burn.profile.{media.Profile}"],
            state,
            ByteSize.Format(media.FreeSectors * 2048L));
    }

    private string ModeText(OpticalBurnMediaKind mode) => _text[$"burn.modeName.{mode}"];

    private string ReasonText(OpticalBurnUnavailableReason reason) => _text[$"burn.reason.{reason}"];

    private string ErrorText(OpticalBurnErrorCode code) => _text[$"burn.error.{code}"];

    private static string Clock(TimeSpan time)
        => time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
