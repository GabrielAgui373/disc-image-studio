using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.Generation;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.App.Avalonia.ViewModels.Export;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.ViewModels.Calibration;

public enum CalibrationSourceKind
{
    /// <summary>The session already linked to the project: regenerating reproduces its target.</summary>
    Linked,

    /// <summary>The project's own disc geometry.</summary>
    Project,

    Preset,
}

/// <summary>Where a target's generation parameters come from.</summary>
public sealed record CalibrationSourceOption(
    CalibrationSourceKind Kind,
    string Id,
    string Title,
    string Description,
    MediaState? Media,
    CalibrationSession? Session);

/// <summary>
/// The Calibration page, step 1 (Task 12.2): choose the disc and where its parameters come from,
/// see the target, and generate it with its <c>.calibration.json</c> snapshot through the
/// transactional publisher. A generated (or opened) session is linked to the project as an
/// undoable edit. Steps 2 and 3 (photo and curve) arrive in Tasks 12.3 and 12.4.
/// </summary>
public sealed partial class CalibrationWorkspaceViewModel : ObservableObject
{
    private const int PreviewSize = 720;
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(250);
    private static readonly FilePickerType SessionFileType = new("Calibration session", ["*.calibration.json"], ["application/json"]);

    private readonly CalibrationTargetRunner _runner;
    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly UserPresetCatalog _presets;
    private readonly IFilePickerService _filePicker;
    private readonly ITextCatalog _text;
    private readonly Debouncer _review;
    private CancellationTokenSource? _reviewing;
    private CancellationTokenSource? _rendering;
    private long _reviewGeneration;
    private bool _isActive;
    private bool _stale = true;
    private bool _rebuilding;
    private string? _customPath;

    /// <summary>The custom path is the linked session's own artifact, not a choice of the user.</summary>
    private bool _customPathFromLink;

    /// <summary>Source to select on the next rebuild (the linked session after a generation).</summary>
    private string? _preferredSourceId;
    private CalibrationSession? _linkedSession;
    private string? _loadedLinkPath;

    public CalibrationWorkspaceViewModel(
        CalibrationTargetRunner runner,
        ProjectLifecycleService projects,
        ProjectEditor editor,
        UserPresetCatalog presets,
        IFilePickerService filePicker,
        ITextCatalog text)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _review = new Debouncer(RefreshDelay, () => _ = ReviewAsync());
        Problems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProblems));
    }

    // ---- Labels ----

    public string StepTargetLabel => _text["calibration.step.target"];

    public string StepPhotoLabel => _text["calibration.step.photo"];

    public string StepCurveLabel => _text["calibration.step.curve"];

    public string StepLaterHint => _text["calibration.step.later"];

    public string TargetTitle => _text["calibration.target.title"];

    public string TargetDescription => _text["calibration.target.description"];

    public string MediaLabel => _text["calibration.media.label"];

    public string CdLabel => _text["export.family.cd"];

    public string DvdLabel => _text["export.family.dvd"];

    public string BluRayLabel => _text["export.family.bd"];

    public string SourceLabel => _text["calibration.source.label"];

    public string PreviewHint => _text["calibration.preview.hint"];

    public string FormatLabel => _text["export.format.title"];

    public string WaveLabel => _text["export.format.wave"];

    public string RawLabel => _text["export.format.raw"];

    public string DestinationTitle => _text["export.destination.title"];

    public string BrowseLabel => _text["export.destination.browse"];

    public string ProblemsTitle => _text["export.problems.title"];

    public string SessionTitle => _text["calibration.session.title"];

    public string OpenSessionLabel => _text["calibration.session.open"];

    public string UnlinkSessionLabel => _text["calibration.session.unlink"];

    // ---- State ----

    public ObservableCollection<CalibrationSourceOption> Sources { get; } = [];

    public ObservableCollection<ExportSummaryRow> Summary { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCd), nameof(IsDvd), nameof(IsBluRay), nameof(IsRawFormat))]
    public partial OpticalDiscFamily Family { get; private set; } = OpticalDiscFamily.Dvd;

    public bool IsCd => Family == OpticalDiscFamily.CompactDisc;

    public bool IsDvd => Family == OpticalDiscFamily.Dvd;

    public bool IsBluRay => Family == OpticalDiscFamily.BluRay;

    [ObservableProperty]
    public partial CalibrationSourceOption? SelectedSource { get; set; }

    /// <summary>The session a Generate would publish: its target, fit defaults and CD interleave.</summary>
    [ObservableProperty]
    public partial CalibrationSession? Session { get; private set; }

    [ObservableProperty]
    public partial Bitmap? TargetImage { get; private set; }

    [ObservableProperty]
    public partial bool IsRendering { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawFormat))]
    public partial bool IsWaveFormat { get; private set; } = true;

    public bool IsRawFormat => IsCd && !IsWaveFormat;

    [ObservableProperty]
    public partial string TargetPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SpaceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSpaceShort { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedSession))]
    public partial string LinkedSessionPath { get; private set; } = string.Empty;

    public bool HasLinkedSession => LinkedSessionPath.Length > 0;

    [ObservableProperty]
    public partial string LinkedSessionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLinkedSessionBroken { get; private set; }

    // ---- Page lifetime ----

    /// <summary>Called when the page is shown or hidden.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active)
        {
            _review.Cancel();
            _reviewing?.Cancel();
            return;
        }

        if (_stale)
        {
            _stale = false;
            Rebuild(resetFamily: Session is null);
        }
    }

    /// <summary>The project changed (geometry, undo of a link, ...): follow it now or on next show.</summary>
    public void RequestRefresh()
    {
        if (!_isActive)
        {
            _stale = true;
            return;
        }

        Rebuild(resetFamily: false);
    }

    /// <summary>Another project was opened or the project closed.</summary>
    public void Clear()
    {
        _review.Cancel();
        _reviewing?.Cancel();
        _rendering?.Cancel();
        ++_reviewGeneration;
        _stale = true;
        _customPath = null;
        _customPathFromLink = false;
        _preferredSourceId = null;
        _linkedSession = null;
        _loadedLinkPath = null;
        Sources.Clear();
        Summary.Clear();
        Problems.Clear();
        SelectedSource = null;
        Session = null;
        TargetImage = null;
        TargetPath = string.Empty;
        SpaceText = string.Empty;
        StatusText = string.Empty;
        IsReady = false;
        LinkedSessionPath = string.Empty;
        LinkedSessionText = string.Empty;
        IsLinkedSessionBroken = false;
        ClearRun();
    }

    // ---- Commands ----

    [RelayCommand]
    private void UseCd() => ChangeFamily(OpticalDiscFamily.CompactDisc);

    [RelayCommand]
    private void UseDvd() => ChangeFamily(OpticalDiscFamily.Dvd);

    [RelayCommand]
    private void UseBluRay() => ChangeFamily(OpticalDiscFamily.BluRay);

    [RelayCommand]
    private void UseWave() => ChangeCdFormat(wave: true);

    [RelayCommand]
    private void UseRaw() => ChangeCdFormat(wave: false);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        string extension = Extension();
        string current = TargetPath.Length > 0 ? TargetPath : DefaultPath(session);
        string? path = await _filePicker.SaveFileAsync(new SaveFilePickerRequest(
            _text["calibration.destination.pickerTitle"],
            SuggestedFileName: Path.GetFileName(current),
            SuggestedDirectory: Path.GetDirectoryName(current),
            DefaultExtension: extension,
            FileTypes: [new FilePickerType(extension.TrimStart('.').ToUpperInvariant(), [$"*{extension}"])]));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            path += extension;
        }

        _customPath = path;
        _customPathFromLink = false;
        TargetPath = path;
        ScheduleReview();
    }

    /// <summary>Links an existing <c>.calibration.json</c> (for example one made by the WPF host).</summary>
    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        IReadOnlyList<string> files = await _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
            _text["calibration.session.pickerTitle"],
            SuggestedDirectory: ProjectDirectory(),
            FileTypes: [SessionFileType]));
        if (files.Count == 0)
        {
            return;
        }

        string path = Path.GetFullPath(files[0]);
        try
        {
            CalibrationSession session = await Task.Run(() => CalibrationSessionStore.Load(path));
            Link(path);
            _linkedSession = session;
            _loadedLinkPath = path;
            Family = FamilyOf(session.Target.Parameters.Kind);
            Rebuild(resetFamily: false);
        }
        catch (CalibrationException exception)
        {
            LinkedSessionText = SessionErrorText(exception);
            IsLinkedSessionBroken = true;
        }
    }

    [RelayCommand]
    private void UnlinkSession()
    {
        if (HasLinkedSession)
        {
            Link(null);
        }
    }

    partial void OnSelectedSourceChanged(CalibrationSourceOption? value)
    {
        if (_rebuilding)
        {
            return;
        }

        UseSource(value);
    }

    // ---- Building the target ----

    private void ChangeFamily(OpticalDiscFamily family)
    {
        if (Family == family)
        {
            return;
        }

        Family = family;
        _customPath = null;
        _customPathFromLink = false;
        Rebuild(resetFamily: false);
    }

    private void ChangeCdFormat(bool wave)
    {
        if (!IsCd || IsWaveFormat == wave)
        {
            return;
        }

        IsWaveFormat = wave;
        if (_customPath is not null)
        {
            _customPath = Path.ChangeExtension(_customPath, Extension());
        }

        TargetPath = Session is { } session ? (_customPath ?? DefaultPath(session)) : string.Empty;
        ScheduleReview();
    }

    /// <summary>Rebuilds the source list for the family and keeps the selection when it still exists.</summary>
    private void Rebuild(bool resetFamily)
    {
        if (_projects.Current is not { } workspace)
        {
            return;
        }

        LoadLinkedSession(workspace);
        if (resetFamily)
        {
            Family = _linkedSession is { } linked ? FamilyOf(linked.Target.Parameters.Kind) : workspace.Media.Family;
        }

        string? keep = _preferredSourceId ?? SelectedSource?.Id;
        _preferredSourceId = null;
        List<CalibrationSourceOption> options = [];
        if (_linkedSession is { } session && FamilyOf(session.Target.Parameters.Kind) == Family)
        {
            options.Add(new(
                CalibrationSourceKind.Linked,
                "linked:" + session.Target.Id,
                string.Format(CultureInfo.CurrentUICulture, _text["calibration.source.linked"], session.Target.Id[..8]),
                _text["calibration.source.linked.description"],
                null,
                session));
        }

        if (workspace.Media.Family == Family)
        {
            options.Add(new(
                CalibrationSourceKind.Project,
                "project",
                _text["calibration.source.project"],
                _text["calibration.source.project.description"],
                workspace.Media,
                null));
        }

        foreach (ProjectPresetDefinition preset in UserPresetCatalog.LoadStandard().Concat(_presets.LoadUserPresets())
                     .Where(preset => preset.Family == Family))
        {
            options.Add(new(
                CalibrationSourceKind.Preset,
                "preset:" + preset.Id,
                preset.IsUserPreset ? preset.DisplayName : _text[$"calibration.source.standard.{FamilyToken(Family)}"],
                preset.IsUserPreset ? preset.Description : _text["calibration.source.standard.description"],
                preset.Media,
                null));
        }

        _rebuilding = true;
        try
        {
            Sources.Clear();
            foreach (CalibrationSourceOption option in options)
            {
                Sources.Add(option);
            }

            CalibrationSourceOption? selected = options.FirstOrDefault(option => option.Id == keep) ?? options.FirstOrDefault();
            // Same target as now: a linked session with the current id, or the same geometry by
            // value (an edit elsewhere in the project copies the media, it does not change it).
            bool same = selected is not null && Session is not null
                        && (selected.Session is { } linked
                            ? linked.Target.Id == Session.Target.Id
                            : SelectedSource is { Session: null } previous && selected.Id == previous.Id && Equals(selected.Media, previous.Media));
            SelectedSource = selected;
            if (!same)
            {
                UseSource(selected);
            }
            else
            {
                ScheduleReview();
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void UseSource(CalibrationSourceOption? source)
    {
        ClearRun();
        Problems.Clear();
        Summary.Clear();
        if (source is null)
        {
            Session = null;
            TargetImage = null;
            TargetPath = string.Empty;
            IsReady = false;
            StatusText = _text["calibration.status.noSource"];
            return;
        }

        try
        {
            // A new target (new id) for a geometry; the linked session keeps its own.
            Session = source.Session ?? CalibrationTargetFactory.NewSession(source.Media!);
        }
        catch (CalibrationException)
        {
            Session = null;
            TargetImage = null;
            TargetPath = string.Empty;
            IsReady = false;
            Problems.Add(_text["calibration.problem.invalidGeometry"]);
            StatusText = _text["export.status.blocked"];
            return;
        }

        CalibrationSession current = Session;
        if (source.Kind != CalibrationSourceKind.Linked && _customPathFromLink)
        {
            _customPath = null;
            _customPathFromLink = false;
        }

        if (source.Kind == CalibrationSourceKind.Linked && _loadedLinkPath is { } linkPath && _customPath is null)
        {
            // Regenerating a linked target writes it back where its session lives.
            _customPath = ArtifactBesideSession(linkPath, current);
            _customPathFromLink = true;
        }

        if (_customPath is not null)
        {
            IsWaveFormat = !Path.GetExtension(_customPath).Equals(".raw", StringComparison.OrdinalIgnoreCase);
        }

        TargetPath = _customPath ?? DefaultPath(current);
        FillSummary(current);
        _ = RenderAsync(current);
        ScheduleReview();
    }

    private void FillSummary(CalibrationSession session)
    {
        CalibrationParameters p = session.Target.Parameters;
        Summary.Clear();
        Summary.Add(new(_text["calibration.row.target"], session.Target.Id[..8]));
        Summary.Add(new(_text["calibration.row.radii"], string.Format(
            CultureInfo.CurrentUICulture, _text["calibration.value.radii"], p.InnerRadiusMm, p.OuterRadiusMm)));
        Summary.Add(new(_text["calibration.row.sectors"], p.Sectors.ToString("N0", CultureInfo.CurrentUICulture)));
        if (p.Kind == CalibrationDiscKind.Dvd)
        {
            Summary.Add(new(_text["calibration.row.pitch"], string.Format(
                CultureInfo.CurrentUICulture, _text["calibration.value.pitch"], p.PitchLinear, p.PitchQuadratic, p.PitchCubic)));
        }

        if (p.Kind == CalibrationDiscKind.Cd)
        {
            Summary.Add(new(_text["calibration.row.interleave"], _text[session.CdInterleave ? "calibration.value.on" : "calibration.value.off"]));
        }

        if (p.Kind == CalibrationDiscKind.BluRay)
        {
            Summary.Add(new(_text["calibration.row.mapping"], _text[p.BluRayInvertMapping ? "calibration.value.inverted" : "calibration.value.normal"]));
        }

        Summary.Add(new(_text["calibration.row.track"], string.Format(
            CultureInfo.CurrentUICulture, _text["calibration.value.track"], p.TrackLengthMm / 1000)));
    }

    private async Task RenderAsync(CalibrationSession session)
    {
        _rendering?.Cancel();
        CancellationTokenSource cancellation = new();
        _rendering = cancellation;
        IsRendering = true;
        try
        {
            CalibrationTarget target = session.Target;
            double canvas = Math.Max(CalibrationReferencePattern.CanvasRadiusMm, target.Parameters.OuterRadiusMm);
            Bitmap bitmap = await Task.Run(
                () =>
                {
                    GrayRaster raster = CalibrationPatternRenderer.RenderDiscView(target, canvas, PreviewSize, cancellationToken: cancellation.Token);
                    return (Bitmap)CompositionBitmap.ToBitmap(raster.Width, raster.Height, raster.Pixels);
                },
                cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(Session, session))
            {
                TargetImage = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_rendering, cancellation))
            {
                IsRendering = false;
            }
        }
    }

    // ---- Plan and destination checks ----

    private void ScheduleReview()
    {
        if (_isActive)
        {
            _review.Schedule();
        }
        else
        {
            _stale = true;
        }
    }

    private async Task ReviewAsync()
    {
        long generation = ++_reviewGeneration;
        _reviewing?.Cancel();
        CancellationTokenSource cancellation = new();
        _reviewing = cancellation;
        if (Session is not { } session || TargetPath.Length == 0 || Phase == ExportPhase.Running)
        {
            return;
        }

        string target = TargetPath;
        CdArtifactFormat format = CdFormat;
        try
        {
            (GenerationPlan Plan, OutputPreflight Preflight) review = await Task.Run(
                async () =>
                {
                    GenerationPlan plan = await _runner.PlanAsync(session, format, cancellation.Token);
                    return (plan, _runner.Preflight(plan, target, ArtifactReplacePolicy.Fail));
                },
                cancellation.Token);
            if (generation == _reviewGeneration)
            {
                Apply(review.Plan, review.Preflight);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (GenerationValidationException exception)
        {
            if (generation == _reviewGeneration)
            {
                Problems.Clear();
                Problems.Add(ExportWorkspaceViewModel.ProblemText(_text, exception.Error));
                IsReady = false;
                StatusText = _text["export.status.blocked"];
            }
        }
    }

    private void Apply(GenerationPlan plan, OutputPreflight preflight)
    {
        Problems.Clear();
        _targetExists = preflight.TargetExists;
        if (Summary.FirstOrDefault(row => row.Label == _text["calibration.row.size"]) is { } previous)
        {
            Summary.Remove(previous);
        }

        Summary.Add(new(_text["calibration.row.size"], string.Format(
            CultureInfo.CurrentUICulture, _text["export.value.size"], ByteSize.Format(plan.EstimatedBytes), plan.EstimatedBytes)));
        SpaceText = preflight.AvailableBytes is { } free
            ? string.Format(CultureInfo.CurrentUICulture, _text["export.space"], ByteSize.Format(preflight.RequiredBytes), ByteSize.Format(free))
            : string.Format(CultureInfo.CurrentUICulture, _text["export.space.unknown"], ByteSize.Format(preflight.RequiredBytes));
        IsSpaceShort = preflight.Problems.Any(problem => problem.Code == "generation.output.insufficient_space");
        foreach (GenerationValidationError problem in preflight.Problems.Where(problem => problem.Code != "generation.output.exists"))
        {
            Problems.Add(ExportWorkspaceViewModel.ProblemText(_text, problem));
        }

        IsReady = Problems.Count == 0;
        StatusText = !IsReady
            ? _text["export.status.blocked"]
            : preflight.TargetExists ? _text["calibration.status.readyReplace"] : _text["calibration.status.ready"];
        GenerateCommand.NotifyCanExecuteChanged();
    }

    // ---- Linked session ----

    private void LoadLinkedSession(ProjectWorkspace workspace)
    {
        string? path = workspace.Identity.CalibrationSessionPath;
        LinkedSessionPath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _linkedSession = null;
            _loadedLinkPath = null;
            LinkedSessionText = _text["calibration.session.none"];
            IsLinkedSessionBroken = false;
            return;
        }

        if (path == _loadedLinkPath && _linkedSession is not null)
        {
            LinkedSessionText = SessionText(_linkedSession);
            return;
        }

        try
        {
            _linkedSession = CalibrationSessionStore.Load(path);
            _loadedLinkPath = path;
            LinkedSessionText = SessionText(_linkedSession);
            IsLinkedSessionBroken = false;
        }
        catch (CalibrationException exception)
        {
            _linkedSession = null;
            _loadedLinkPath = null;
            LinkedSessionText = SessionErrorText(exception);
            IsLinkedSessionBroken = true;
        }
    }

    private string SessionText(CalibrationSession session)
    {
        CalibrationParameters p = session.Target.Parameters;
        string stage = session.Photo switch
        {
            null => _text["calibration.session.stage.target"],
            { RectificationConfirmed: false } => _text["calibration.session.stage.photo"],
            _ => string.Format(
                CultureInfo.CurrentUICulture,
                _text["calibration.session.stage.curve"],
                session.Photo.Traces.Sum(trace => trace.Points.Count)),
        };
        return string.Format(
            CultureInfo.CurrentUICulture,
            _text["calibration.session.summary"],
            _text[$"export.family.{FamilyToken(FamilyOf(p.Kind))}"],
            session.Target.Id[..8],
            stage);
    }

    private string SessionErrorText(CalibrationException exception)
    {
        string key = $"calibration.error.{exception.Code}";
        try
        {
            return _text[key];
        }
        catch (KeyNotFoundException)
        {
            return string.Format(CultureInfo.CurrentUICulture, _text["calibration.error.unknown"], exception.Code);
        }
    }

    /// <summary>An undoable project edit; saved with the project.</summary>
    private void Link(string? sessionPath)
        => _editor.Apply(HistoryLabels.CalibrationSession, workspace => DesignEdits.SetCalibrationSession(workspace, sessionPath));

    // ---- Paths ----

    private CdArtifactFormat CdFormat => IsWaveFormat ? CdArtifactFormat.WaveAudio : CdArtifactFormat.RawAudio;

    private string Extension() => IsCd ? (IsWaveFormat ? ".wav" : ".raw") : ".iso";

    private string? ProjectDirectory()
    {
        if (_projects.ProjectFilePath is { Length: > 0 } project)
        {
            return Path.GetDirectoryName(Path.GetFullPath(project));
        }

        return _projects.Current?.Delivery.OutputPath is { Length: > 0 } output
            ? Path.GetDirectoryName(Path.GetFullPath(output))
            : null;
    }

    /// <summary>The WPF file name, beside the project.</summary>
    private string DefaultPath(CalibrationSession session)
        => Path.Combine(ProjectDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CalibrationTargetFactory.FileName(session.Target, Extension()));

    /// <summary><c>calibration-x.calibration.json</c> → <c>calibration-x.iso</c> (or <c>.wav</c>).</summary>
    private string ArtifactBesideSession(string sessionPath, CalibrationSession session)
    {
        string name = Path.GetFileName(sessionPath);
        string stem = name.EndsWith(CalibrationSessionStore.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^CalibrationSessionStore.Extension.Length]
            : Path.GetFileNameWithoutExtension(name);
        string directory = Path.GetDirectoryName(sessionPath) ?? string.Empty;
        string extension = session.Target.Parameters.Kind != CalibrationDiscKind.Cd
            ? ".iso"
            : File.Exists(Path.Combine(directory, stem + ".raw")) ? ".raw" : ".wav";
        return Path.Combine(directory, stem + extension);
    }

    private static OpticalDiscFamily FamilyOf(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => OpticalDiscFamily.CompactDisc,
        CalibrationDiscKind.BluRay => OpticalDiscFamily.BluRay,
        _ => OpticalDiscFamily.Dvd,
    };

    private static string FamilyToken(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.CompactDisc => "cd",
        OpticalDiscFamily.BluRay => "bd",
        _ => "dvd",
    };
}
