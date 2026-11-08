using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Preview;

namespace DiscImageStudio.App.Avalonia.ViewModels.Preview;

/// <summary>A resolution the user can pick, with its label.</summary>
public sealed record PreviewSizeOption(int Size, string Label);

/// <summary>An effective parameter as shown in the details list.</summary>
public sealed record PreviewParameterItem(string Name, string Value);

/// <summary>
/// The Preview page. It only holds the selection (projection or generated artifact), the
/// parameters (resolution, draft source, overlays), the page lifecycle and the presentation;
/// every calculation runs in the preview services through <see cref="PreviewCoordinator"/>,
/// which cancels an older request as soon as a newer one starts.
/// </summary>
public sealed partial class PreviewWorkspaceViewModel : ObservableObject
{
    private const string Channel = "preview-page";
    private const int DraftSourceSize = 4096;
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(300);

    private readonly PreviewCoordinator _previews;
    private readonly ProjectLifecycleService _projects;
    private readonly ITextCatalog _text;
    private readonly IFilePickerService _filePicker;
    private readonly IClipboardService _clipboard;
    private readonly Debouncer _refresh;
    private PreviewResult? _result;
    private long _generation;
    private long _presentation;
    private bool _isActive;
    private bool _stale = true;
    private OpticalDiscFamily? _family;
    private bool _syncing;

    public PreviewWorkspaceViewModel(
        PreviewCoordinator previews,
        ProjectLifecycleService projects,
        ITextCatalog text,
        IFilePickerService filePicker,
        IClipboardService clipboard)
    {
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _refresh = new Debouncer(RefreshDelay, () => _ = RefreshAsync());
        ProjectionLabel = text["preview.mode.projection"];
        ArtifactLabel = text["preview.mode.artifact"];
        ModeTitle = text["preview.mode.title"];
        ResolutionLabel = text["preview.resolution"];
        DraftLabel = text["preview.draft"];
        DraftHint = text["preview.draftHint"];
        OverlaysTitle = text["preview.overlays.title"];
        FileSystemLabel = text["preview.overlays.fileSystem"];
        UsefulRingLabel = text["preview.overlays.usefulRing"];
        WarningsTitle = text["preview.warnings.title"];
        ParametersTitle = text["preview.parameters.title"];
        RefreshLabel = text["preview.refresh"];
        RenderingLabel = text["preview.rendering"];
        ExportLabel = text["preview.export"];
        CopyDiagnosticsLabel = text["preview.copyDiagnostics"];
        Warnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasWarnings));
    }

    public string ProjectionLabel { get; }

    public string ArtifactLabel { get; }

    public string ModeTitle { get; }

    public string ResolutionLabel { get; }

    public string DraftLabel { get; }

    public string DraftHint { get; }

    public string OverlaysTitle { get; }

    public string FileSystemLabel { get; }

    public string UsefulRingLabel { get; }

    public string WarningsTitle { get; }

    public string ParametersTitle { get; }

    public string RefreshLabel { get; }

    public string RenderingLabel { get; }

    public string ExportLabel { get; }

    public string CopyDiagnosticsLabel { get; }

    /// <summary>Outcome of the last export or copy, shown under the actions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionStatus))]
    public partial string ActionStatus { get; private set; } = string.Empty;

    public bool HasActionStatus => ActionStatus.Length > 0;

    /// <summary>Shown when the estimated peak memory is large for this computer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCostWarning))]
    public partial string CostWarning { get; private set; } = string.Empty;

    public bool HasCostWarning => CostWarning.Length > 0;

    public ObservableCollection<PreviewSizeOption> Sizes { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<PreviewParameterItem> Parameters { get; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>False shows the projection of the current design; true reads the generated file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectionMode), nameof(SourceDescription))]
    public partial bool IsArtifactMode { get; set; }

    public bool IsProjectionMode
    {
        get => !IsArtifactMode;
        set => IsArtifactMode = !value;
    }

    [ObservableProperty]
    public partial PreviewSizeOption? SelectedSize { get; set; }

    /// <summary>Projection only: read a 4096 px source instead of the 8192 px generation source.</summary>
    [ObservableProperty]
    public partial bool UseDraftSource { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowFileSystem { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowUsefulRing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    public partial IImage? Image { get; private set; }

    public bool HasImage => Image is not null;

    [ObservableProperty]
    public partial bool IsRendering { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CostEstimate { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfidence))]
    public partial string ConfidenceLabel { get; private set; } = string.Empty;

    public bool HasConfidence => ConfidenceLabel.Length > 0;

    [ObservableProperty]
    public partial bool IsApproximate { get; private set; }

    [ObservableProperty]
    public partial bool HasFileSystemOverlay { get; private set; }

    /// <summary>What the picture is made from: the design, or the artifact file at its path.</summary>
    public string SourceDescription
    {
        get
        {
            if (!IsArtifactMode)
            {
                return _text["preview.source.projection"];
            }

            string? path = _projects.Current is { } workspace
                ? ArtifactPreviewService.ArtifactPath(workspace, _projects.ProjectFilePath)
                : null;
            return path is null
                ? _text["preview.source.noOutput"]
                : string.Format(CultureInfo.CurrentUICulture, _text["preview.source.artifact"], path);
        }
    }

    /// <summary>Called when the Preview page is shown or hidden; showing it renders a stale preview.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active)
        {
            _refresh.Cancel();
            _previews.Cancel(Channel);
            return;
        }

        SyncProject();
        if (_stale)
        {
            _stale = false;
            _refresh.Schedule();
        }
    }

    /// <summary>The project changed: render again if the page is visible, otherwise on next show.</summary>
    public void RequestRefresh()
    {
        OnPropertyChanged(nameof(SourceDescription));
        if (!_isActive)
        {
            _stale = true;
            return;
        }

        SyncProject();
        _refresh.Schedule();
    }

    /// <summary>Forgets the picture, for example when the project closes.</summary>
    public void Clear()
    {
        _refresh.Cancel();
        _previews.Cancel(Channel);
        ++_generation;
        ++_presentation;
        _result = null;
        _family = null;
        _stale = true;
        Image = null;
        Error = null;
        Summary = string.Empty;
        ConfidenceLabel = string.Empty;
        IsApproximate = false;
        IsRendering = false;
        HasFileSystemOverlay = false;
        Warnings.Clear();
        Parameters.Clear();
    }

    [RelayCommand]
    private void ShowProjection() => IsArtifactMode = false;

    [RelayCommand]
    private void ShowArtifact() => IsArtifactMode = true;

    [RelayCommand]
    private void Refresh()
    {
        _refresh.Cancel();
        _ = RefreshAsync();
    }

    partial void OnIsArtifactModeChanged(bool value) => ParametersChanged();

    partial void OnSelectedSizeChanged(PreviewSizeOption? value) => ParametersChanged();

    partial void OnUseDraftSourceChanged(bool value) => ParametersChanged();

    // Overlays are drawn on the last result; they never recompute the preview.
    partial void OnShowFileSystemChanged(bool value) => _ = PresentAsync();

    partial void OnShowUsefulRingChanged(bool value) => _ = PresentAsync();

    private void ParametersChanged()
    {
        if (_syncing)
        {
            return;
        }

        UpdateCostEstimate();
        if (_isActive)
        {
            _refresh.Schedule();
        }
        else
        {
            _stale = true;
        }
    }

    /// <summary>Resets the resolution choices to the family's defaults when the family changes.</summary>
    private void SyncProject()
    {
        OpticalDiscFamily? family = _projects.Current?.Media.Family;
        if (family == _family)
        {
            UpdateCostEstimate();
            return;
        }

        _family = family;
        _syncing = true;
        try
        {
            Sizes.Clear();
            if (family is { } current)
            {
                int standard = PreviewDefaults.OutputSize(current);
                foreach (int size in new[] { 512, 1024, standard, 2048, 4096, 8192 }.Distinct().Order())
                {
                    string label = size == standard
                        ? string.Format(CultureInfo.CurrentUICulture, _text["preview.size.default"], size)
                        : string.Format(CultureInfo.CurrentUICulture, _text["preview.size"], size);
                    Sizes.Add(new PreviewSizeOption(size, label));
                }

                SelectedSize = Sizes.First(option => option.Size == standard);
            }
            else
            {
                SelectedSize = null;
            }
        }
        finally
        {
            _syncing = false;
        }

        UpdateCostEstimate();
    }

    private void UpdateCostEstimate()
    {
        if (_projects.Current is not { } workspace || SelectedSize is not { } size)
        {
            CostEstimate = string.Empty;
            return;
        }

        OpticalDiscFamily family = workspace.Media.Family;
        PreviewMode mode = IsArtifactMode ? PreviewMode.Artifact : PreviewMode.Projection;
        PreviewCostEstimate estimate = PreviewCostEstimator.Estimate(
            family,
            mode,
            size.Size,
            workspace.Media.Sectors,
            PreviewDefaults.SamplesPerSector(family),
            UseDraftSource ? DraftSourceSize : PreviewDefaults.MaximumSize);
        // Showing the result adds the overlay copy and the bitmap, one BGRA buffer each.
        long displayBytes = 2L * size.Size * size.Size * 4;
        long peak = estimate.PeakBytes + displayBytes;
        CostEstimate = string.Format(
            CultureInfo.CurrentUICulture,
            _text["preview.cost"],
            Math.Max(0.1, estimate.Duration.TotalSeconds),
            Math.Ceiling(peak / (1024.0 * 1024.0)));

        // Above most of the memory the system can still hand out, the preview competes with the
        // rest of the desktop and may be killed by the kernel's out-of-memory handling.
        long available = SystemMemory.AvailableBytes();
        CostWarning = available > 0 && peak > available * 0.8 ? _text["preview.costWarning"] : string.Empty;
    }

    private bool CanExport() => _result is not null && !IsRendering;

    /// <summary>Saves exactly the pixels on screen (view, media and overlays included) as a PNG.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportPngAsync()
    {
        if (_result is not { } result)
        {
            return;
        }

        string projectName = _projects.Current?.Identity.Name ?? string.Empty;
        string? path = await _filePicker.SaveFileAsync(new SaveFilePickerRequest(
            _text["preview.export.pickerTitle"],
            SuggestedFileName: PreviewExport.SuggestedFileName(projectName, result),
            SuggestedDirectory: _projects.ProjectFilePath is { } project ? Path.GetDirectoryName(project) : null,
            DefaultExtension: ".png",
            FileTypes: [new FilePickerType("PNG", ["*.png"], ["image/png"])]));
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        byte[]? pixels = DisplayedPixels();
        if (pixels is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => PreviewExport.SavePng(path, result.Size, pixels));
            ActionStatus = string.Format(CultureInfo.CurrentUICulture, _text["preview.export.done"], path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ActionStatus = string.Format(CultureInfo.CurrentUICulture, _text["preview.export.failed"], exception.Message);
        }
    }

    /// <summary>Copies the parameters, confidence and warnings behind the picture as text.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task CopyDiagnosticsAsync()
    {
        if (_result is not { } result)
        {
            return;
        }

        string text = PreviewExport.Diagnostics(
            _projects.Current?.Identity.Name ?? string.Empty,
            result with { Warnings = [.. result.Warnings.Select(NamedLayers)] },
            ShowFileSystem,
            ShowUsefulRing);
        await _clipboard.SetTextAsync(text);
        ActionStatus = _text["preview.copyDiagnostics.done"];
    }

    partial void OnIsRenderingChanged(bool value) => NotifyActionsChanged();

    private void NotifyActionsChanged()
    {
        ExportPngCommand.NotifyCanExecuteChanged();
        CopyDiagnosticsCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        long generation = ++_generation;
        if (_projects.Current is not { } workspace || SelectedSize is not { } size)
        {
            Clear();
            return;
        }

        PreviewRequest request = new(
            workspace,
            _projects.ProjectFilePath,
            IsArtifactMode ? PreviewMode.Artifact : PreviewMode.Projection,
            size.Size,
            Revision: generation,
            SourceSize: UseDraftSource ? DraftSourceSize : null);
        IsRendering = true;
        Error = null;
        try
        {
            PreviewResult? result = await _previews.RenderLatestAsync(Channel, request);
            if (result is null || generation != _generation)
            {
                return;
            }

            _result = result;
            ApplyDetails(result);
            await PresentAsync();
        }
        catch (PreviewFailedException failure)
        {
            if (generation == _generation)
            {
                ShowFailure(_text[$"preview.failure.{failure.Code}"]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or ArgumentException
                                              or InvalidOperationException or AggregateException)
        {
            if (generation == _generation)
            {
                ShowFailure(_text["preview.failure.unexpected"]);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRendering = false;
            }
        }
    }

    /// <summary>Draws the selected overlays onto the last result and shows it.</summary>
    private async Task PresentAsync()
    {
        if (_result is not { } result)
        {
            return;
        }

        long presentation = ++_presentation;
        bool fileSystem = ShowFileSystem;
        bool usefulRing = ShowUsefulRing;
        IImage bitmap = await Task.Run(() =>
        {
            byte[] pixels = PreviewOverlayPainter.Paint(result, fileSystem, usefulRing);
            return (IImage)CompositionBitmap.ToBitmap(new BgraRaster(result.Size, result.Size, pixels));
        });
        if (presentation == _presentation && ReferenceEquals(result, _result))
        {
            Image = bitmap;
        }
    }

    /// <summary>The exact pixels on screen (overlays included), for export.</summary>
    public byte[]? DisplayedPixels()
        => _result is { } result ? PreviewOverlayPainter.Paint(result, ShowFileSystem, ShowUsefulRing) : null;

    public PreviewResult? Result => _result;

    private void ShowFailure(string message)
    {
        _result = null;
        ++_presentation;
        Image = null;
        Error = message;
        Summary = string.Empty;
        ConfidenceLabel = string.Empty;
        IsApproximate = false;
        HasFileSystemOverlay = false;
        Warnings.Clear();
        Parameters.Clear();
    }

    private PreviewWarning NamedLayers(PreviewWarning warning)
        => warning.Code == PreviewWarningCode.LayersNotPainted ? warning with { Detail = LayerNames(warning.Detail) } : warning;

    private string? LayerNames(string? ids)
    {
        if (ids is null || _projects.Current is not { } workspace)
        {
            return ids;
        }

        return string.Join(", ", ids.Split(", ").Select(id =>
            workspace.Design.Layers.FirstOrDefault(layer => layer.Id == id)?.Name ?? id));
    }

    private void ApplyDetails(PreviewResult result)
    {
        Summary = string.Format(
            CultureInfo.CurrentUICulture,
            _text[result.Mode == PreviewMode.Projection ? "preview.summary.projection" : "preview.summary.artifact"],
            result.Size,
            result.Elapsed.TotalSeconds);
        IsApproximate = result.Confidence == PreviewConfidence.Approximate;
        ConfidenceLabel = _text[IsApproximate ? "preview.confidence.approximate" : "preview.confidence.exact"];
        HasFileSystemOverlay = result.Overlays.Any(overlay => overlay.Kind == PreviewOverlayKind.FileSystem);
        Warnings.Clear();
        foreach (PreviewWarning warning in result.Warnings)
        {
            string text = _text[$"preview.warning.{warning.Code}"];
            string? detail = warning.Code switch
            {
                // The services report layer ids; people know their layers by name.
                PreviewWarningCode.LayersNotPainted => LayerNames(warning.Detail),
                PreviewWarningCode.DraftSource => null,
                _ => warning.Detail,
            };
            Warnings.Add(detail is { Length: > 0 }
                ? string.Format(CultureInfo.CurrentUICulture, "{0}: {1}", text, detail)
                : text);
        }

        Parameters.Clear();
        foreach (PreviewParameter parameter in result.Parameters)
        {
            Parameters.Add(new PreviewParameterItem(parameter.Key, parameter.Value));
        }
    }
}
