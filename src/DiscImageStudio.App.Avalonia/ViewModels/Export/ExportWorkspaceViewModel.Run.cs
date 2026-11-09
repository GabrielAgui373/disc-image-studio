using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Generation;
using DiscImageStudio.Application.Jobs;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.ViewModels.Export;

/// <summary>Where the Export page is in its flow.</summary>
public enum ExportPhase
{
    Review,
    ConfirmReplace,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>
/// The run half of the Export page (Tasks 11.3 and 11.4): confirmation before replacing, the
/// export job with stage, progress, throughput and time left, cancellation, and the outcome with
/// its checks and next steps.
/// </summary>
public sealed partial class ExportWorkspaceViewModel
{
    private ExportRunner? _runner;
    private JobCoordinator? _jobs;
    private DiscImageStudio.Application.Ports.IExternalLauncher? _launcher;
    private DiscImageStudio.Application.Ports.IClipboardService? _clipboard;
    private Guid? _jobId;
    private bool _targetExists;
    private string? _reviewFingerprint;

    /// <summary>Raised by "Open in Preview": the shell shows the Preview page reading the file back.</summary>
    public event Action? OpenInPreviewRequested;

    /// <summary>Raised by "Continue to Burn".</summary>
    public event Action? GoToBurnRequested;

    public ObservableCollection<string> ResultChecks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewPhase), nameof(IsConfirmingReplace), nameof(IsRunning), nameof(HasOutcome), nameof(IsSucceeded))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial ExportPhase Phase { get; private set; } = ExportPhase.Review;

    public bool IsReviewPhase => Phase == ExportPhase.Review;

    public bool IsConfirmingReplace => Phase == ExportPhase.ConfirmReplace;

    public bool IsRunning => Phase == ExportPhase.Running;

    public bool HasOutcome => Phase is ExportPhase.Succeeded or ExportPhase.Failed or ExportPhase.Canceled;

    public bool IsSucceeded => Phase == ExportPhase.Succeeded;

    /// <summary>Read the written file a second time and compare it with the hash taken while writing.</summary>
    [ObservableProperty]
    public partial bool VerifyHash { get; set; }

    [ObservableProperty]
    public partial string StageText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressKnown))]
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

    public string GenerateLabel => _text["export.generate"];

    public string ReplaceTitle => _text["export.replace.title"];

    public string ReplaceText => string.Format(CultureInfo.CurrentUICulture, _text["export.replace.text"], Path.GetFileName(TargetPath));

    public string ReplaceLabel => _text["export.replace.confirm"];

    public string KeepLabel => _text["export.replace.keep"];

    public string CancelRunLabel => _text["export.run.cancel"];

    public string VerifyHashLabel => _text["export.verifyHash"];

    public string OpenFolderLabel => _text["export.result.openFolder"];

    public string CopyPathLabel => _text["export.result.copyPath"];

    public string OpenInPreviewLabel => _text["export.result.openInPreview"];

    public string GoToBurnLabel => _text["export.result.goToBurn"];

    public string DoneLabel => _text["export.result.done"];

    /// <summary>Called once by the shell: what Generate and the result actions need.</summary>
    public void AttachRunner(
        ExportRunner runner,
        JobCoordinator jobs,
        DiscImageStudio.Application.Ports.IExternalLauncher launcher,
        DiscImageStudio.Application.Ports.IClipboardService clipboard)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerate() => _runner is not null && IsReady && Phase is ExportPhase.Review or ExportPhase.Succeeded or ExportPhase.Failed or ExportPhase.Canceled;

    partial void OnIsReadyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

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
    private void Done()
    {
        Phase = ExportPhase.Review;
        ResultChecks.Clear();
        ActionStatus = string.Empty;
    }

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

    [RelayCommand]
    private void OpenInPreview() => OpenInPreviewRequested?.Invoke();

    [RelayCommand]
    private void GoToBurn() => GoToBurnRequested?.Invoke();

    private async Task RunAsync(bool replaceExisting)
    {
        if (_runner is not { } runner || _jobs is not { } jobs || _projects.Current is not { } workspace)
        {
            return;
        }

        Phase = ExportPhase.Running;
        ResultChecks.Clear();
        ActionStatus = string.Empty;
        ProgressFraction = null;
        ProgressText = string.Empty;
        StageText = StageLabel(ExportStage.Composing);
        ExportRunRequest request = new(workspace, _projects.ProjectFilePath, replaceExisting, VerifyHash, _reviewFingerprint);
        ExportOutcome? outcome = null;
        string jobName = string.Format(CultureInfo.CurrentUICulture, _text["export.job.name"], workspace.Identity.Name);
        ApplicationResult<JobCompletion> result = await jobs.StartJobAsync(
            new JobRequest(jobName),
            async context =>
            {
                _jobId = context.JobId;
                Progress<ExportProgress> progress = new(update => ShowProgress(update, context.Progress));
                outcome = await runner.RunAsync(request, progress, context.CancellationToken);
            });
        _jobId = null;

        if (result.Succeeded && outcome is { } done)
        {
            ShowSuccess(done);
        }
        else if (result.Error?.Code == ApplicationErrorCode.JobCanceled)
        {
            OutcomeTitle = _text["export.result.canceledTitle"];
            OutcomeText = _text["export.result.canceledText"];
            Phase = ExportPhase.Canceled;
        }
        else
        {
            OutcomeTitle = _text["export.result.failedTitle"];
            OutcomeText = FailureText(result.Error);
            Phase = ExportPhase.Failed;
        }

        // The destination may now exist (or the plan may have to change): plan again.
        RequestRefresh();
    }

    private void ShowProgress(ExportProgress update, IProgress<JobProgress>? job)
    {
        StageText = StageLabel(update.Stage);
        ProgressFraction = update.Fraction;
        OnPropertyChanged(nameof(ProgressPercent));
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

    private void ShowSuccess(ExportOutcome outcome)
    {
        PublishedArtifact artifact = outcome.Artifact;
        ResultPath = artifact.Artifact.Path;
        OutcomeTitle = _text["export.result.succeededTitle"];
        OutcomeText = string.Format(
            CultureInfo.CurrentUICulture,
            _text["export.result.succeededText"],
            ByteSize.Format(artifact.Artifact.Bytes),
            Clock(outcome.Elapsed),
            artifact.Artifact.Sha256[..16]);
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
    {
        Exception? exception = error?.Exception;
        return exception switch
        {
            GenerationValidationException validation => ProblemText(validation.Error),
            ExportSourceChangedException => _text["export.failure.sourceChanged"],
            ArtifactValidationException failed => string.Format(
                CultureInfo.CurrentUICulture,
                _text["export.failure.validation"],
                CheckLabel(failed.Report.FirstFailure?.Code ?? "?")),
            UnauthorizedAccessException => _text["export.failure.permission"],
            IOException io => string.Format(CultureInfo.CurrentUICulture, _text["export.failure.io"], io.Message),
            _ => string.Format(CultureInfo.CurrentUICulture, _text["export.failure.unexpected"], error?.Detail ?? string.Empty),
        };
    }

    private string StageLabel(ExportStage stage) => _text[$"export.stage.{stage}"];

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
