using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.Generation;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.Generation;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.App.Avalonia.ViewModels.Export;

/// <summary>One line of the plan summary.</summary>
public sealed record ExportSummaryRow(string Label, string Value);

/// <summary>
/// The Export page before generation (Task 11.2): the plan of what will be produced, the
/// destination with its checks, and what blocks or deserves attention. Planning runs only while
/// the page is visible, after a short pause, and a newer refresh cancels an older one. Nothing is
/// generated here; the run itself is Task 11.3.
/// </summary>
public sealed partial class ExportWorkspaceViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(300);

    private readonly ExportPlanner _planner;
    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly IFilePickerService _filePicker;
    private readonly ITextCatalog _text;
    private readonly Debouncer _refresh;
    private CancellationTokenSource? _planning;
    private long _generation;
    private bool _isActive;
    private bool _stale = true;
    private string? _expectedExtension;

    public ExportWorkspaceViewModel(
        ExportPlanner planner,
        ProjectLifecycleService projects,
        ProjectEditor editor,
        IFilePickerService filePicker,
        ITextCatalog text)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _refresh = new Debouncer(RefreshDelay, () => _ = RefreshAsync());
        PlanTitle = text["export.plan.title"];
        PlanDescription = text["export.plan.description"];
        DestinationTitle = text["export.destination.title"];
        BrowseLabel = text["export.destination.browse"];
        FormatLabel = text["export.format.title"];
        WaveLabel = text["export.format.wave"];
        RawLabel = text["export.format.raw"];
        FormatHint = text["export.format.hint"];
        ProblemsTitle = text["export.problems.title"];
        NotesTitle = text["export.notes.title"];
        PlanningLabel = text["export.planning"];
        Problems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProblems));
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
    }

    public string PlanTitle { get; }

    public string PlanDescription { get; }

    public string DestinationTitle { get; }

    public string BrowseLabel { get; }

    public string FormatLabel { get; }

    public string WaveLabel { get; }

    public string RawLabel { get; }

    public string FormatHint { get; }

    public string ProblemsTitle { get; }

    public string NotesTitle { get; }

    public string PlanningLabel { get; }

    public ObservableCollection<ExportSummaryRow> Summary { get; } = [];

    /// <summary>What stops generation until fixed.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>What does not stop generation but deserves a look.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    [ObservableProperty]
    public partial bool IsPlanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial GenerationPlan? Plan { get; private set; }

    public bool HasPlan => Plan is not null;

    [ObservableProperty]
    public partial string TargetPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SpaceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSpaceShort { get; private set; }

    /// <summary>Overall verdict: ready, or what to do first.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    /// <summary>CD without a data folder: the track can be written as WAVE or raw samples.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawFormat))]
    public partial bool IsCdAudio { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawFormat))]
    public partial bool IsWaveFormat { get; private set; }

    public bool IsRawFormat => IsCdAudio && !IsWaveFormat;

    /// <summary>Offered when the destination has the wrong extension for the plan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtensionFix))]
    public partial string ExtensionFixLabel { get; private set; } = string.Empty;

    public bool HasExtensionFix => ExtensionFixLabel.Length > 0;

    /// <summary>Called when the Export page is shown or hidden.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active)
        {
            _refresh.Cancel();
            _planning?.Cancel();
            return;
        }

        if (_stale)
        {
            _stale = false;
            _refresh.Schedule();
        }
    }

    /// <summary>The project changed: plan again if the page is visible, otherwise on next show.</summary>
    public void RequestRefresh()
    {
        if (!_isActive)
        {
            _stale = true;
            return;
        }

        _refresh.Schedule();
    }

    public void Clear()
    {
        _refresh.Cancel();
        _planning?.Cancel();
        ++_generation;
        _stale = true;
        Plan = null;
        IsPlanning = false;
        TargetPath = string.Empty;
        SpaceText = string.Empty;
        IsSpaceShort = false;
        StatusText = string.Empty;
        IsReady = false;
        IsCdAudio = false;
        ExtensionFixLabel = string.Empty;
        Summary.Clear();
        Problems.Clear();
        Notes.Clear();
    }

    [RelayCommand]
    private void Refresh()
    {
        _refresh.Cancel();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_projects.Current is not { } workspace)
        {
            return;
        }

        string extension = ExpectedExtension(workspace);
        string current = TargetPath.Length > 0 ? TargetPath : workspace.Delivery.OutputPath;
        string? path = await _filePicker.SaveFileAsync(new SaveFilePickerRequest(
            _text["export.destination.pickerTitle"],
            SuggestedFileName: Path.GetFileName(Path.ChangeExtension(current, extension)),
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

        SetOutput(path);
    }

    [RelayCommand]
    private void UseWave() => ChangeExtension(".wav");

    [RelayCommand]
    private void UseRaw() => ChangeExtension(".raw");

    [RelayCommand]
    private void FixExtension()
    {
        if (_expectedExtension is { } extension)
        {
            ChangeExtension(extension);
        }
    }

    private void ChangeExtension(string extension)
    {
        if (_projects.Current is { } workspace
            && !Path.GetExtension(workspace.Delivery.OutputPath).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            SetOutput(Path.ChangeExtension(workspace.Delivery.OutputPath, extension));
        }
    }

    // An undoable project edit: Preview reads the same path back.
    private void SetOutput(string path)
        => _editor.Apply(HistoryLabels.OutputFile, workspace => DesignEdits.SetOutputPath(workspace, path));

    /// <summary>The extension generation requires for the project's current settings.</summary>
    private static string ExpectedExtension(ProjectWorkspace workspace)
    {
        bool hasData = !string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory);
        if (workspace.Media is CdState && !hasData)
        {
            return Path.GetExtension(workspace.Delivery.OutputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                ? ".wav"
                : ".raw";
        }

        return ".iso";
    }

    private async Task RefreshAsync()
    {
        long generation = ++_generation;
        _planning?.Cancel();
        _planning?.Dispose();
        CancellationTokenSource cancellation = new();
        _planning = cancellation;
        if (_projects.Current is not { } workspace)
        {
            Clear();
            return;
        }

        IsPlanning = true;
        try
        {
            ExportReview review = await _planner.ReviewAsync(workspace, _projects.ProjectFilePath, cancellation.Token);
            if (generation == _generation)
            {
                Apply(workspace, review);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or ArgumentException
                                              or InvalidOperationException)
        {
            if (generation == _generation)
            {
                Plan = null;
                Summary.Clear();
                Problems.Clear();
                Problems.Add(string.Format(CultureInfo.CurrentUICulture, _text["export.problem.unexpected"], exception.Message));
                IsReady = false;
                StatusText = _text["export.status.blocked"];
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsPlanning = false;
            }
        }
    }

    private void Apply(ProjectWorkspace workspace, ExportReview review)
    {
        Plan = review.Plan;
        TargetPath = review.TargetPath;
        _targetExists = review.Preflight?.TargetExists == true;
        _reviewFingerprint = review.DataFingerprint;
        bool hasData = !string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory);
        IsCdAudio = workspace.Media is CdState && !hasData;
        IsWaveFormat = Path.GetExtension(review.TargetPath).Equals(".wav", StringComparison.OrdinalIgnoreCase);

        Summary.Clear();
        Problems.Clear();
        Notes.Clear();
        ExtensionFixLabel = string.Empty;
        _expectedExtension = null;

        Summary.Add(Row("export.row.source", string.Format(
            CultureInfo.CurrentUICulture, _text["export.value.source"], review.PaintedLayers, review.VisibleLayers)));
        Summary.Add(Row("export.row.media", MediaText(workspace.Media)));
        if (review.Plan is { } plan)
        {
            Summary.Add(Row("export.row.format", FormatText(plan, review.TargetPath)));
            Summary.Add(Row("export.row.files", FilesText(plan, workspace)));
            Summary.Add(Row("export.row.drawing", DrawingText(plan, workspace.Media)));
            Summary.Add(Row("export.row.tone", ToneText(workspace.Tone.Profile, workspace.Media)));
            if (workspace.Media is CdState cd && plan.Filesystem == GenerationFilesystem.CdDa)
            {
                Summary.Add(Row("export.row.track", TrackText(cd, IsWaveFormat)));
            }

            Summary.Add(Row("export.row.size", string.Format(
                CultureInfo.CurrentUICulture, _text["export.value.size"], ByteSize.Format(plan.EstimatedBytes), plan.EstimatedBytes)));
        }
        else if (review.PlanError is { } error)
        {
            Problems.Add(ProblemText(error));
        }

        if (review.Preflight is { } preflight)
        {
            SpaceText = preflight.AvailableBytes is { } free
                ? string.Format(CultureInfo.CurrentUICulture, _text["export.space"], ByteSize.Format(preflight.RequiredBytes), ByteSize.Format(free))
                : string.Format(CultureInfo.CurrentUICulture, _text["export.space.unknown"], ByteSize.Format(preflight.RequiredBytes));
            IsSpaceShort = preflight.Problems.Any(problem => problem.Code == "generation.output.insufficient_space");
            foreach (GenerationValidationError problem in preflight.Problems)
            {
                if (problem.Code == "generation.output.exists")
                {
                    // Not a blocker: generating will ask before replacing (Task 11.3).
                    Notes.Add(ExistingText(preflight));
                    continue;
                }

                if (problem.Code == "generation.output.extension"
                    && problem.Parameters.TryGetValue("expected", out object? expected)
                    && expected is string extension)
                {
                    _expectedExtension = extension;
                    ExtensionFixLabel = string.Format(CultureInfo.CurrentUICulture, _text["export.fixExtension"], extension);
                }

                Problems.Add(ProblemText(problem));
            }
        }
        else
        {
            SpaceText = string.Empty;
            IsSpaceShort = false;
        }

        if (review.PaintedLayers == 0)
        {
            Notes.Add(_text["export.note.nothingDrawn"]);
        }

        foreach (CompositionIssue issue in review.SourceIssues)
        {
            string layer = workspace.Design.Layers.FirstOrDefault(item => item.Id == issue.LayerId)?.Name ?? issue.LayerId;
            Notes.Add(string.Format(CultureInfo.CurrentUICulture, _text["export.note.layerNotDrawn"], layer));
        }

        IsReady = review.Plan is not null && Problems.Count == 0;
        StatusText = IsReady ? _text["export.status.ready"] : _text["export.status.blocked"];
    }

    private ExportSummaryRow Row(string labelKey, string value) => new(_text[labelKey], value);

    private string MediaText(MediaState media)
    {
        string family = _text[media.Family switch
        {
            OpticalDiscFamily.CompactDisc => "export.family.cd",
            OpticalDiscFamily.Dvd => "export.family.dvd",
            _ => "export.family.bd",
        }];
        return string.Format(
            CultureInfo.CurrentUICulture,
            _text["export.value.media"],
            family,
            media.InnerRadiusMm,
            media.OuterRadiusMm,
            media.Sectors);
    }

    private string FormatText(GenerationPlan plan, string target)
        => _text[plan.Filesystem switch
        {
            GenerationFilesystem.CdDa when Path.GetExtension(target).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                => "export.value.format.cdWave",
            GenerationFilesystem.CdDa => "export.value.format.cdRaw",
            GenerationFilesystem.Iso9660Joliet => "export.value.format.iso",
            GenerationFilesystem.Udf201 => "export.value.format.udf",
            _ => "export.value.format.imageOnly",
        }];

    private string FilesText(GenerationPlan plan, ProjectWorkspace workspace)
    {
        if (plan.FileCount == 0)
        {
            return _text["export.value.files.none"];
        }

        return string.Format(
            CultureInfo.CurrentUICulture,
            _text[workspace.Delivery.FilesAtOuterRing ? "export.value.files.outer" : "export.value.files.inner"],
            plan.FileCount,
            ByteSize.Format(plan.FileBytes));
    }

    private string DrawingText(GenerationPlan plan, MediaState media)
    {
        double share = media.Sectors <= 0 ? 0 : 100.0 * plan.DrawingRange.SectorCount / media.Sectors;
        return string.Format(
            CultureInfo.CurrentUICulture,
            _text["export.value.drawing"],
            plan.DrawingRange.StartSector,
            plan.DrawingRange.EndSectorExclusive,
            share);
    }

    private string ToneText(ToneProfile tone, MediaState media)
    {
        int levels = media is CdState ? tone.Levels : ToneProfile.TwoLevels;
        string text = string.Format(CultureInfo.CurrentUICulture, _text["export.value.tone"], tone.DitherStrength * 100, levels);
        return tone.SrgbLinearization ? text + _text["export.value.tone.srgb"] : text;
    }

    private string TrackText(CdState cd, bool wave)
        => string.Format(
            CultureInfo.CurrentUICulture,
            _text["export.value.track"],
            _text[cd.Interleave ? "export.value.on" : "export.value.off"],
            _text[wave && cd.WriteCue ? "export.value.yes" : "export.value.no"]);

    private string ExistingText(OutputPreflight preflight)
    {
        string companions = preflight.ExistingCompanions.Count == 0
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentUICulture,
                _text["export.note.existsCompanions"],
                string.Join(", ", preflight.ExistingCompanions.Select(Path.GetFileName)));
        return _text["export.note.exists"] + companions;
    }

    private string ProblemText(GenerationValidationError error)
    {
        if (error.Code == "generation.output.insufficient_space"
            && error.Parameters.TryGetValue("requiredBytes", out object? required)
            && error.Parameters.TryGetValue("availableBytes", out object? available)
            && required is long need
            && available is long free)
        {
            return string.Format(CultureInfo.CurrentUICulture, _text["export.problem.generation.output.insufficient_space"], ByteSize.Format(need), ByteSize.Format(free));
        }

        try
        {
            return _text[$"export.problem.{error.Code}"];
        }
        catch (KeyNotFoundException)
        {
            // A code added to the pipeline later still reaches the user, with its stable id.
            return string.Format(CultureInfo.CurrentUICulture, _text["export.problem.unknown"], error.Code);
        }
    }
}
