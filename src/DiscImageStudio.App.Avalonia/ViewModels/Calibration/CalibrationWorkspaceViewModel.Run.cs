using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Generation;
using DiscImageStudio.App.Avalonia.ViewModels.Export;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.ViewModels.Calibration;

/// <summary>
/// Generating the target (Task 12.2): confirmation before replacing, the job with stage and
/// progress, cancellation, and the outcome. A successful run links the published session to
/// the project; a canceled or failed one changes neither the files nor the project.
/// </summary>
public sealed partial class CalibrationWorkspaceViewModel
{
    private JobCoordinator? _jobs;
    private IExternalLauncher? _launcher;
    private IClipboardService? _clipboard;
    private Guid? _jobId;
    private bool _targetExists;

    public ObservableCollection<string> ResultChecks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingReplace), nameof(IsRunning), nameof(HasOutcome), nameof(IsSucceeded))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial ExportPhase Phase { get; private set; } = ExportPhase.Review;

    public bool IsConfirmingReplace => Phase == ExportPhase.ConfirmReplace;

    public bool IsRunning => Phase == ExportPhase.Running;

    public bool HasOutcome => Phase is ExportPhase.Succeeded or ExportPhase.Failed or ExportPhase.Canceled;

    public bool IsSucceeded => Phase == ExportPhase.Succeeded;

    [ObservableProperty]
    public partial bool VerifyHash { get; set; }

    [ObservableProperty]
    public partial string StageText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressKnown), nameof(ProgressPercent))]
    public partial double? ProgressFraction { get; private set; }

    public bool IsProgressKnown => ProgressFraction is not null;

    public double ProgressPercent => (ProgressFraction ?? 0) * 100;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OutcomeTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OutcomeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ActionStatus { get; private set; } = string.Empty;

    public string GenerateLabel => _text["calibration.generate"];

    public string VerifyHashLabel => _text["export.verifyHash"];

    public string ReplaceTitle => _text["export.replace.title"];

    public string ReplaceText => string.Format(CultureInfo.CurrentUICulture, _text["export.replace.text"], Path.GetFileName(TargetPath));

    public string ReplaceLabel => _text["export.replace.confirm"];

    public string KeepLabel => _text["export.replace.keep"];

    public string CancelRunLabel => _text["export.run.cancel"];

    public string OpenFolderLabel => _text["export.result.openFolder"];

    public string CopyPathLabel => _text["export.result.copyPath"];

    public string DoneLabel => _text["export.result.done"];

    /// <summary>Called once by the shell.</summary>
    public void AttachRunner(JobCoordinator jobs, IExternalLauncher launcher, IClipboardService clipboard)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerate() => _jobs is not null && Session is not null && IsReady && Phase != ExportPhase.Running;

    partial void OnIsReadyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

    partial void OnSessionChanged(CalibrationSession? value) => GenerateCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_targetExists)
        {
            OnPropertyChanged(nameof(ReplaceText));
            Phase = ExportPhase.ConfirmReplace;
            return;
        }

        await RunAsync(replaceExisting: false);
    }

    [RelayCommand]
    private async Task ConfirmReplaceAsync() => await RunAsync(replaceExisting: true);

    [RelayCommand]
    private void KeepExisting() => Phase = ExportPhase.Review;

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
    private void Done() => ClearRun();

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (_launcher is not null && Path.GetDirectoryName(ResultPath) is { Length: > 0 } folder)
        {
            bool opened = await _launcher.OpenPathAsync(folder);
            ActionStatus = opened ? string.Empty : _text["export.result.openFolderFailed"];
        }
    }

    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (_clipboard is not null && ResultPath.Length > 0)
        {
            await _clipboard.SetTextAsync(ResultPath);
            ActionStatus = _text["export.result.pathCopied"];
        }
    }

    private void ClearRun()
    {
        if (Phase == ExportPhase.Running)
        {
            return;
        }

        Phase = ExportPhase.Review;
        ResultChecks.Clear();
        ActionStatus = string.Empty;
        OutcomeTitle = string.Empty;
        OutcomeText = string.Empty;
        ResultPath = string.Empty;
    }

    private async Task RunAsync(bool replaceExisting)
    {
        if (_jobs is not { } jobs || Session is not { } session || TargetPath.Length == 0)
        {
            return;
        }

        _review.Cancel();
        _reviewing?.Cancel();
        Phase = ExportPhase.Running;
        ResultChecks.Clear();
        ActionStatus = string.Empty;
        ProgressFraction = null;
        ProgressText = string.Empty;
        StageText = _text["export.stage.Planning"];
        CalibrationTargetRunRequest request = new(session, CdFormat, TargetPath, replaceExisting, VerifyHash);
        CalibrationTargetOutcome? outcome = null;
        string jobName = string.Format(CultureInfo.CurrentUICulture, _text["calibration.job.name"], session.Target.Id[..8]);
        ApplicationResult<JobCompletion> result = await jobs.StartJobAsync(
            new JobRequest(jobName),
            async context =>
            {
                _jobId = context.JobId;
                Progress<ExportProgress> progress = new(update => ShowProgress(update, context.Progress));
                outcome = await _runner.RunAsync(request, progress, context.CancellationToken);
            });
        _jobId = null;

        if (result.Succeeded && outcome is { } done)
        {
            ShowSuccess(done);
            // The project now points at this target's session (undoable, saved with the project).
            if (_projects.Current?.Identity.CalibrationSessionPath != done.SessionPath)
            {
                _linkedSession = session;
                _loadedLinkPath = done.SessionPath;
                _customPath = done.Artifact.Artifact.Path;
                _customPathFromLink = true;
                _preferredSourceId = "linked:" + session.Target.Id;
                Link(done.SessionPath);
            }
        }
        else if (result.Error?.Code == ApplicationErrorCode.JobCanceled)
        {
            OutcomeTitle = _text["export.result.canceledTitle"];
            OutcomeText = _text["calibration.result.canceledText"];
            Phase = ExportPhase.Canceled;
        }
        else
        {
            OutcomeTitle = _text["export.result.failedTitle"];
            OutcomeText = FailureText(result.Error);
            Phase = ExportPhase.Failed;
        }

        ScheduleReview();
    }

    private void ShowProgress(ExportProgress update, IProgress<JobProgress>? job)
    {
        StageText = _text[$"export.stage.{update.Stage}"];
        ProgressFraction = update.Fraction;
        ProgressText = update.Stage == ExportStage.Writing && update.TotalBytes > 0
            ? string.Format(
                CultureInfo.CurrentUICulture,
                _text[update.Remaining is null ? "export.progress.noEta" : "export.progress"],
                ByteSize.Format(update.CompletedBytes),
                ByteSize.Format(update.TotalBytes),
                ByteSize.Format((long)update.BytesPerSecond),
                Clock(update.Elapsed),
                Clock(update.Remaining ?? TimeSpan.Zero))
            : string.Format(CultureInfo.CurrentUICulture, _text["export.progress.elapsed"], Clock(update.Elapsed));
        job?.Report(new JobProgress(update.Stage.ToString(), $"{StageText} · {ProgressText}", update.Fraction));
    }

    private void ShowSuccess(CalibrationTargetOutcome outcome)
    {
        PublishedArtifact artifact = outcome.Artifact;
        ResultPath = artifact.Artifact.Path;
        OutcomeTitle = _text["calibration.result.succeededTitle"];
        OutcomeText = string.Format(
            CultureInfo.CurrentUICulture,
            _text["calibration.result.succeededText"],
            ByteSize.Format(artifact.Artifact.Bytes),
            Clock(outcome.Elapsed),
            Path.GetFileName(outcome.SessionPath));
        ResultChecks.Clear();
        foreach (ArtifactCheck check in outcome.Validation.Checks)
        {
            ResultChecks.Add(string.Format(CultureInfo.CurrentUICulture, _text["export.check.passed"], CheckLabel(check.Code)));
        }

        foreach (PublishedFile companion in artifact.Companions)
        {
            ResultChecks.Add(string.Format(CultureInfo.CurrentUICulture, _text["export.result.companion"], Path.GetFileName(companion.Path)));
        }

        Phase = ExportPhase.Succeeded;
    }

    private string FailureText(ApplicationError? error)
        => error?.Exception switch
        {
            GenerationValidationException validation => ExportWorkspaceViewModel.ProblemText(_text, validation.Error),
            ArtifactValidationException failed => string.Format(
                CultureInfo.CurrentUICulture,
                _text["export.failure.validation"],
                CheckLabel(failed.Report.FirstFailure?.Code ?? "?")),
            UnauthorizedAccessException => _text["export.failure.permission"],
            IOException io => string.Format(CultureInfo.CurrentUICulture, _text["export.failure.io"], io.Message),
            _ when error?.Code == ApplicationErrorCode.JobConflict => _text["calibration.failure.busy"],
            _ => string.Format(CultureInfo.CurrentUICulture, _text["export.failure.unexpected"], error?.Detail ?? string.Empty),
        };

    private string CheckLabel(string code)
    {
        try
        {
            return _text[$"export.check.{code}"];
        }
        catch (KeyNotFoundException)
        {
            return code;
        }
    }

    private static string Clock(TimeSpan time)
        => time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
