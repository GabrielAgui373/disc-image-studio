using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.ViewModels.Export;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.App.Avalonia.ViewModels.Calibration;

/// <summary>A search box for the two radii; the automatic one widens until the optimum is inside.</summary>
public sealed record CalibrationRangeOption(string Label, double RadiusRangeMm, bool Auto);

/// <summary>One fitted candidate as listed on the page.</summary>
public sealed record CalibrationCandidateItem(CalibrationCandidate Candidate, string Title, string Detail, bool Accepted, string Badge);

/// <summary>
/// Step 3 (Task 12.4) and the closing of the calibration (Task 12.5): curve points on the
/// rectified photo, the fit with automatic widening of the search box, the candidates with their
/// residuals and the prediction of the whole curve over the points. Applying needs an acceptable
/// residual and the user's explicit confirmation that the predicted curve follows the points;
/// it is one undoable step that sets the generation geometry and records the measurement Preview
/// reads with. The result can also be kept as a user preset and exported as a report.
/// </summary>
public sealed partial class CalibrationWorkspaceViewModel
{
    private static readonly FilePickerType ReportFileType = new("Calibration report", ["*.json"], ["application/json"]);

    private CalibrationFitRequest? _fitRequest;
    private CalibrationFitOutcome? _fit;
    private CalibrationPrediction? _prediction;
    private long _fitTraceRevision = -1;
    private CancellationTokenSource? _fitting;
    private DateTimeOffset? _appliedUtc;
    private string? _savedPreset;

    public bool IsCurveStep => CurrentStep == CalibrationStep.Curve;

    /// <summary>The curve needs a confirmed rectification (loaded, or confirmed in the session file).</summary>
    public bool CanUseCurveStep => CanUsePhotoStep && (_photo.Confirmed || _linkedSession?.Photo?.RectificationConfirmed == true);

    public IReadOnlyList<CalibrationRangeOption> SearchRanges => _searchRanges ??=
    [
        new(_text["calibration.curve.range.auto"], 0.02, true),
        new("±0.02 mm", 0.02, false),
        new("±0.1 mm", 0.1, false),
        new("±0.5 mm", 0.5, false),
    ];

    private IReadOnlyList<CalibrationRangeOption>? _searchRanges;

    [ObservableProperty]
    public partial CalibrationRangeOption? SelectedRange { get; set; }

    /// <summary>DVD only: also fit the three track-pitch terms.</summary>
    [ObservableProperty]
    public partial bool FitPitch { get; set; } = true;

    public bool IsDvdSession => _photoOwner?.Target.Parameters.Kind == CalibrationDiscKind.Dvd;

    public ObservableCollection<CalibrationCandidateItem> Candidates { get; } = [];

    public ObservableCollection<string> FitWarnings { get; } = [];

    public ObservableCollection<ExportSummaryRow> CandidateRows { get; } = [];

    [ObservableProperty]
    public partial CalibrationCandidateItem? SelectedCandidate { get; set; }

    [ObservableProperty]
    public partial bool IsFitting { get; private set; }

    [ObservableProperty]
    public partial string FitProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FitAssessment { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFitAccepted { get; private set; }

    [ObservableProperty]
    public partial bool IsPredictionReady { get; private set; }

    /// <summary>The user checked that the predicted curve follows the points along its whole length.</summary>
    [ObservableProperty]
    public partial bool CurveConfirmed { get; set; }

    [ObservableProperty]
    public partial string ApplyStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MeasurementText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMeasurement { get; private set; }

    [ObservableProperty]
    public partial string PresetName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PresetStatus { get; private set; } = string.Empty;

    /// <summary>Include the photo and the file paths in the report (off by default).</summary>
    [ObservableProperty]
    public partial bool ReportIncludesPersonal { get; set; }

    [ObservableProperty]
    public partial string ReportStatus { get; private set; } = string.Empty;

    public bool HasFit => _fit is not null;

    public bool HasFitWarnings => FitWarnings.Count > 0;

    public bool CanApply => ApplyBlockedReason() is null;

    public string ApplyBlockedText => ApplyBlockedReason() ?? string.Empty;

    public string TraceCountText => string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.points"], _photo.TracePoints.Count);

    // ---- Labels ----

    public string CurveTitle => _text["calibration.curve.title"];

    public string CurveDescription => _text["calibration.curve.description"];

    public string RangeLabel => _text["calibration.curve.range"];

    public string FitPitchLabel => _text["calibration.curve.fitPitch"];

    public string SolveLabel => _text["calibration.curve.solve"];

    public string ClearTraceLabel => _text["calibration.curve.clear"];

    public string RemovePointLabel => _text["calibration.curve.removePoint"];

    public string CandidatesLabel => _text["calibration.curve.candidates"];

    public string ConfirmCurveLabel => _text["calibration.curve.confirm"];

    public string ApplyLabel => _text["calibration.apply"];

    public string MeasurementTitle => _text["calibration.measurement.title"];

    public string ClearMeasurementLabel => _text["calibration.measurement.clear"];

    public string PresetLabel => _text["calibration.preset.label"];

    public string PresetWatermark => _text["calibration.preset.watermark"];

    public string SavePresetLabel => _text["calibration.preset.save"];

    public string ReportLabel => _text["calibration.report.export"];

    public string ReportPersonalLabel => _text["calibration.report.personal"];

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanUseCurveStep))]
    private async Task ShowCurveStepAsync()
    {
        CurrentStep = CalibrationStep.Curve;
        await EnsurePhotoLoadedAsync();
        RefreshCurveState();
    }

    [RelayCommand]
    private void ClearTrace() => _photo.ClearTrace();

    [RelayCommand]
    private void RemoveSelectedPoint() => PhotoCanvas.RemoveSelectedTrace();

    [RelayCommand]
    private async Task SolveAsync()
    {
        if (_photoOwner is not { } session || _photo.TracePoints.Count < 6 || IsFitting)
        {
            return;
        }

        InvalidateFit();
        CalibrationTarget target = session.Target;
        CalibrationRangeOption range = SelectedRange ?? SearchRanges[0];
        CalibrationFitRequest request = new(
            target,
            [new CalibrationCrossTrace(0, [.. _photo.TracePoints])],
            CurrentFitOptions(session) with { RadiusRangeMm = range.RadiusRangeMm },
            range.Auto);
        long traceRevision = _photo.TraceRevision;
        _fitting = new CancellationTokenSource();
        IsFitting = true;
        FitProgressText = _text["calibration.curve.fitting"];
        RefreshCurveState();
        try
        {
            Progress<CalibrationWorkProgress> progress = new(update => FitProgressText = string.Format(
                CultureInfo.CurrentUICulture,
                _text["calibration.curve.fittingBox"],
                update.Attempt,
                update.Attempts,
                update.RadiusRangeMm ?? 0));
            CalibrationFitOutcome outcome = await _fitters.FitAsync(request, progress, _fitting.Token);
            if (traceRevision != _photo.TraceRevision)
            {
                return;
            }

            _fitRequest = request;
            _fit = outcome;
            _fitTraceRevision = traceRevision;
            ShowFit(outcome);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsFitting = false;
            FitProgressText = string.Empty;
            RefreshCurveState();
        }
    }

    [RelayCommand]
    private void CancelFit() => _fitting?.Cancel();

    /// <summary>One undoable step: generation geometry and measurement from the selected candidate.</summary>
    [RelayCommand]
    private async Task ApplyCalibrationAsync()
    {
        if (!CanApply || SelectedCandidate is not { } item || _photoOwner is not { } session)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            ApplicationResult<Application.Editing.EditOutcome> result = _editor.Apply(
                HistoryLabels.ApplyCalibration,
                workspace => CalibrationEdits.ApplyMeasurement(workspace, session.Target.Id, item.Candidate.Fit, now));
            if (!result.Succeeded)
            {
                ApplyStatus = string.Format(CultureInfo.CurrentUICulture, _text["calibration.apply.failed"], result.Error?.Detail ?? result.Error?.Code.ToString());
                return;
            }
        }
        catch (CalibrationException exception)
        {
            ApplyStatus = ErrorText(exception);
            return;
        }

        _appliedUtc = now;
        ApplyStatus = _text["calibration.apply.done"];
        // The session keeps the points and settings the measurement came from.
        await SavePhotoSessionAsync();
        RefreshCurveState();
    }

    [RelayCommand]
    private void ClearMeasurement()
    {
        if (_projects.Current?.Measurement is not null)
        {
            _editor.Apply(HistoryLabels.ClearMeasurement, CalibrationEdits.ClearMeasurement);
        }
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (SelectedCandidate is not { } item || _photoOwner is not { } session || !IsResultUsable())
        {
            PresetStatus = _text["calibration.preset.notReady"];
            return;
        }

        CalibrationParameters p = item.Candidate.Fit.Parameters;
        MediaState media = p.Kind switch
        {
            CalibrationDiscKind.Cd => new CdState(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, writeCue: true, interleave: session.CdInterleave),
            CalibrationDiscKind.BluRay => new BluRayState(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.BluRayInvertMapping),
            _ => new DvdState(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.PitchLinear, p.PitchQuadratic, p.PitchCubic),
        };
        string name = PresetName.Trim();
        string description = string.Format(
            CultureInfo.CurrentUICulture,
            _text["calibration.preset.description"],
            session.Target.Id[..8],
            DateTimeOffset.Now.ToString("d", CultureInfo.CurrentUICulture),
            item.Candidate.Fit.RmsErrorMm);
        ApplicationResult<ProjectPresetDefinition> saved = _presets.SaveUserPreset(name, description, media);
        if (saved.Succeeded)
        {
            _savedPreset = saved.Value.DisplayName;
            PresetStatus = string.Format(CultureInfo.CurrentUICulture, _text["calibration.preset.saved"], saved.Value.DisplayName);
        }
        else
        {
            PresetStatus = saved.Error?.Code == ApplicationErrorCode.InvalidRequest
                ? _text["calibration.preset.nameRequired"]
                : string.Format(CultureInfo.CurrentUICulture, _text["calibration.preset.failed"], saved.Error?.Detail ?? string.Empty);
        }
    }

    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (_photoOwner is not { } session)
        {
            return;
        }

        string directory = Path.GetDirectoryName(_photoOwnerPath ?? string.Empty) is { Length: > 0 } folder ? folder : ProjectDirectory() ?? string.Empty;
        string? path = await _filePicker.SaveFileAsync(new SaveFilePickerRequest(
            _text["calibration.report.pickerTitle"],
            SuggestedFileName: $"calibration-{session.Target.Id[..8]}.report.json",
            SuggestedDirectory: directory,
            DefaultExtension: ".json",
            FileTypes: [ReportFileType]));
        if (!string.IsNullOrWhiteSpace(path))
        {
            await ExportReportToAsync(path);
        }
    }

    /// <summary>Writes the report (also used by the harness, which has no picker).</summary>
    public async Task ExportReportToAsync(string path)
    {
        if (_photoOwner is not { } session)
        {
            return;
        }

        CalibrationReportInput input = new(
            ComposeSession(session),
            _photo,
            _fitRequest,
            _fit,
            _prediction,
            new CalibrationDecision(SelectedCandidate?.Candidate.Rank, CurveConfirmed, _appliedUtc is not null, _appliedUtc, _savedPreset),
            _photoOwnerPath,
            _projects.ProjectFilePath);
        bool personal = ReportIncludesPersonal;
        try
        {
            await Task.Run(() => CalibrationReport.Save(path, input, DateTimeOffset.UtcNow, personal));
            ReportStatus = string.Format(CultureInfo.CurrentUICulture, _text["calibration.report.saved"], Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReportStatus = string.Format(CultureInfo.CurrentUICulture, _text["export.failure.io"], exception.Message);
        }
    }

    // ---- State ----

    partial void OnSelectedRangeChanged(CalibrationRangeOption? value) => InvalidateFit();

    partial void OnFitPitchChanged(bool value) => InvalidateFit();

    partial void OnCurveConfirmedChanged(bool value) => RefreshCurveState();

    partial void OnSelectedCandidateChanged(CalibrationCandidateItem? value)
    {
        CurveConfirmed = false;
        IsPredictionReady = false;
        _prediction = null;
        PhotoCanvas.SetPrediction(null, null);
        FillCandidateRows(value);
        UpdateAssessment();
        RefreshCurveState();
        if (value is not null)
        {
            _ = PredictAsync(value);
        }
    }

    private async Task PredictAsync(CalibrationCandidateItem item)
    {
        if (_photoOwner is not { } session)
        {
            return;
        }

        long revision = _photo.TraceRevision;
        try
        {
            CalibrationPrediction prediction = await _fitters.PredictAsync(session.Target, item.Candidate.Fit, [.. _photo.TracePoints]);
            if (!ReferenceEquals(SelectedCandidate, item) || revision != _photo.TraceRevision)
            {
                return;
            }

            _prediction = prediction;
            PhotoCanvas.SetPrediction(prediction.Curve, prediction.OutlierIndices);
            IsPredictionReady = true;
            UpdateAssessment();
            RefreshCurveState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (CalibrationException)
        {
            IsPredictionReady = false;
        }
    }

    /// <summary>Called when the photo state changes: a curve edit makes the fit stale.</summary>
    private void OnTraceMaybeChanged()
    {
        if (_fit is not null && _fitTraceRevision != _photo.TraceRevision)
        {
            InvalidateFit();
        }

        if (IsFitting && _fitTraceRevision != _photo.TraceRevision)
        {
            _fitting?.Cancel();
        }

        OnPropertyChanged(nameof(TraceCountText));
        RefreshCurveState();
    }

    private void InvalidateFit()
    {
        _fit = null;
        _fitRequest = null;
        _prediction = null;
        _fitTraceRevision = -1;
        Candidates.Clear();
        FitWarnings.Clear();
        CandidateRows.Clear();
        SelectedCandidate = null;
        IsPredictionReady = false;
        CurveConfirmed = false;
        IsFitAccepted = false;
        FitAssessment = string.Empty;
        ApplyStatus = string.Empty;
        PhotoCanvas.SetPrediction(null, null);
        OnPropertyChanged(nameof(HasFit));
        OnPropertyChanged(nameof(HasFitWarnings));
        RefreshCurveState();
    }

    private void ShowFit(CalibrationFitOutcome outcome)
    {
        Candidates.Clear();
        foreach (CalibrationCandidate candidate in outcome.Candidates)
        {
            Candidates.Add(new CalibrationCandidateItem(
                candidate,
                string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.candidate"], candidate.Rank),
                string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.candidateDetail"], candidate.Fit.RmsErrorMm, candidate.Fit.MaxErrorMm, candidate.Fit.OutlierCount),
                candidate.ResidualAccepted,
                _text[candidate.ResidualAccepted ? "calibration.curve.badge.ok" : "calibration.curve.badge.rejected"]));
        }

        FitWarnings.Clear();
        foreach (string warning in outcome.Result.Warnings)
        {
            string code = CalibrationFitMessages.Code(warning);
            FitWarnings.Add(code == "other"
                ? string.Format(CultureInfo.CurrentUICulture, _text["calibration.warning.other"], warning)
                : _text[$"calibration.warning.{code}"]);
        }

        OnPropertyChanged(nameof(HasFit));
        OnPropertyChanged(nameof(HasFitWarnings));
        SelectedCandidate = Candidates.FirstOrDefault();
        UpdateAssessment();
    }

    private void UpdateAssessment()
    {
        if (_fit is not { } fit)
        {
            FitAssessment = string.Empty;
            IsFitAccepted = false;
            return;
        }

        if (!fit.Result.Succeeded)
        {
            FitAssessment = _text["calibration.curve.failed"];
            IsFitAccepted = false;
            return;
        }

        if (SelectedCandidate is not { } item)
        {
            FitAssessment = _text["calibration.curve.noCandidate"];
            IsFitAccepted = false;
            return;
        }

        CalibrationFitCandidate candidate = item.Candidate.Fit;
        IsFitAccepted = item.Accepted;
        string text = string.Format(
            CultureInfo.CurrentUICulture,
            _text[item.Accepted ? "calibration.curve.accepted" : "calibration.curve.rejected"],
            candidate.RmsErrorMm,
            candidate.MaxErrorMm,
            CalibrationMediaRules.MaximumRmsErrorMm,
            CalibrationMediaRules.MaximumPointErrorMm);
        if (fit.RangeExpanded)
        {
            text += " " + string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.expanded"], fit.RadiusRangeMm);
        }

        if (_prediction is { OutlierIndices.Count: > 0 } prediction)
        {
            text += " " + string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.outliers"], prediction.OutlierIndices.Count, prediction.OutlierThresholdMm);
        }

        FitAssessment = text;
    }

    private void FillCandidateRows(CalibrationCandidateItem? item)
    {
        CandidateRows.Clear();
        if (item is null || _photoOwner is not { } session)
        {
            return;
        }

        CalibrationParameters generated = session.Target.Parameters;
        CalibrationParameters fitted = item.Candidate.Fit.Parameters;
        CandidateRows.Add(new(_text["calibration.row.inner"], Delta(fitted.InnerRadiusMm, generated.InnerRadiusMm)));
        CandidateRows.Add(new(_text["calibration.row.outer"], Delta(fitted.OuterRadiusMm, generated.OuterRadiusMm)));
        if (fitted.Kind == CalibrationDiscKind.Dvd)
        {
            CandidateRows.Add(new(_text["calibration.row.pitch"], string.Format(
                CultureInfo.CurrentUICulture, _text["calibration.value.pitch"], fitted.PitchLinear, fitted.PitchQuadratic, fitted.PitchCubic)));
        }

        CandidateRows.Add(new(_text["calibration.row.rotation"], string.Format(
            CultureInfo.CurrentUICulture, "{0:0.##}°", item.Candidate.Fit.RotationRadians * 180 / Math.PI)));
        CandidateRows.Add(new(_text["calibration.row.residual"], item.Detail));
    }

    private string Delta(double fitted, double generated)
        => string.Format(CultureInfo.CurrentUICulture, _text["calibration.value.delta"], fitted, fitted - generated);

    /// <summary>The fit settings the page would use now (saved with the session, like the WPF wizard).</summary>
    private CalibrationFitOptions CurrentFitOptions(CalibrationSession session)
    {
        CalibrationRangeOption range = SelectedRange ?? SearchRanges[0];
        CalibrationFitOptions basis = session.FitOptions ?? CalibrationTargetFactory.DefaultFitOptions(session.Target);
        return basis with
        {
            SearchCenter = basis.SearchCenter ?? session.Target.Parameters,
            RadiusRangeMm = range.RadiusRangeMm,
            AutoRange = range.Auto,
            FitDvdPitch = FitPitch,
            AdaptiveRobustScale = true,
        };
    }

    /// <summary>The owner session with the current photo, points and fit settings.</summary>
    private CalibrationSession ComposeSession(CalibrationSession owner)
        => owner with { Photo = _photo.ToSession(), FitOptions = CurrentFitOptions(owner) };

    /// <summary>Restores the range and pitch switches a session was saved with.</summary>
    private void LoadFitSettings(CalibrationSession session)
    {
        CalibrationFitOptions? options = session.FitOptions;
        bool auto = options?.AutoRange ?? true;
        double range = options?.RadiusRangeMm ?? 0.02;
        SelectedRange = SearchRanges.FirstOrDefault(option => option.Auto == auto && (auto || option.RadiusRangeMm == range)) ?? SearchRanges[0];
        FitPitch = options?.FitDvdPitch ?? true;
    }

    private bool IsResultUsable()
        => _fit is { Result.Succeeded: true } && SelectedCandidate is { Accepted: true } && IsPredictionReady && CurveConfirmed
           && _fitTraceRevision == _photo.TraceRevision && !IsFitting;

    private string? ApplyBlockedReason()
    {
        if (_fit is null)
        {
            return _text["calibration.apply.needsFit"];
        }

        if (!_fit.Result.Succeeded || SelectedCandidate is null)
        {
            return _text["calibration.apply.noResult"];
        }

        if (!SelectedCandidate.Accepted)
        {
            return _text["calibration.apply.residual"];
        }

        if (!IsPredictionReady)
        {
            return _text["calibration.apply.predicting"];
        }

        if (!CurveConfirmed)
        {
            return _text["calibration.apply.confirm"];
        }

        if (_projects.Current is { } workspace && _photoOwner is { } session
            && workspace.Media.Family != FamilyOf(session.Target.Parameters.Kind))
        {
            return _text["calibration.apply.family"];
        }

        return IsResultUsable() ? null : _text["calibration.apply.stale"];
    }

    private void RefreshCurveState()
    {
        PhotoCanvas.IsTracing = IsCurveStep;
        PhotoCanvas.CanTrace = IsCurveStep && _photo.Confirmed && !IsPhotoBusy && !IsFitting;
        ShowCurveStepCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseCurveStep));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyBlockedText));
        OnPropertyChanged(nameof(IsDvdSession));
        OnPropertyChanged(nameof(TraceCountText));
        if (_projects.Current?.Measurement is { } measurement)
        {
            HasMeasurement = true;
            MeasurementText = string.Format(
                CultureInfo.CurrentUICulture,
                _text["calibration.measurement.text"],
                measurement.InnerRadiusMm,
                measurement.OuterRadiusMm,
                measurement.TargetId[..Math.Min(8, measurement.TargetId.Length)],
                measurement.MeasuredUtc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture),
                measurement.RmsErrorMm);
        }
        else
        {
            HasMeasurement = false;
            MeasurementText = _text["calibration.measurement.none"];
        }
    }
}
