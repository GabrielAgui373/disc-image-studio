using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>One layer as the canvas sees it; document order is bottom-first.</summary>
public sealed record CanvasLayer(
    string Id,
    string Name,
    bool Visible,
    double Opacity,
    LayerTransform Transform,
    double BaseWidthMm,
    double BaseHeightMm,
    IImage? Image,
    bool IsMissing,
    bool IsLoading)
{
    public Point[] Corners() => LayerGeometry.Corners(Transform, BaseWidthMm, BaseHeightMm);

    public Point Centre => new(Transform.TranslateXMm, Transform.TranslateYMm);
}

/// <summary>
/// State behind the design canvas. View state (zoom, pan, selection) lives here and never reaches
/// the project; every persistent change goes through <see cref="ProjectEditor"/>: a pointer drag
/// is one history step, and a burst of arrow-key nudges is grouped into one step that commits
/// after a short pause or before any other command.
/// </summary>
public sealed partial class DesignCanvasViewModel : ObservableObject
{
    public const double MinimumScale = 0.01;
    public const double NudgeMm = 0.1;
    public const double LargeNudgeMm = 1.0;
    private static readonly TimeSpan NudgeCommitDelay = TimeSpan.FromMilliseconds(700);

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly LayerBitmapCache _bitmaps;
    private readonly ITextCatalog _text;
    private readonly DispatcherTimer _nudgeTimer;
    private ProjectEditor.DragEdit? _drag;
    private string? _dragLayerId;
    private Vector _nudgeOffset;

    public DesignCanvasViewModel(
        ProjectLifecycleService projects,
        ProjectEditor editor,
        LayerBitmapCache bitmaps,
        ITextCatalog text)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _bitmaps = bitmaps ?? throw new ArgumentNullException(nameof(bitmaps));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _bitmaps.Changed += (_, _) => Refresh();
        _nudgeTimer = new DispatcherTimer { Interval = NudgeCommitDelay };
        _nudgeTimer.Tick += (_, _) => CommitPendingEdits();
        CanvasTitle = text["design.canvas.title"];
        EmptyHint = text["design.canvas.empty"];
        MissingLabel = text["design.canvas.missing"];
        HintText = text["design.canvas.hint"];
        ZoomInLabel = text["design.zoom.in"];
        ZoomOutLabel = text["design.zoom.out"];
        FitLabel = text["design.zoom.fit"];
        UpdateZoomPercent();
    }

    /// <summary>Raised when the canvas must repaint (layers, selection or view changed).</summary>
    public event EventHandler? InvalidateRequested;

    public CanvasViewport Viewport { get; } = new();

    public IReadOnlyList<CanvasLayer> Layers { get; private set; } = [];

    public double ContentInnerRadiusMm { get; private set; }

    public double ContentOuterRadiusMm { get; private set; }

    public bool HasLayers => Layers.Count > 0;

    public string CanvasTitle { get; }

    public string EmptyHint { get; }

    public string MissingLabel { get; }

    public string HintText { get; }

    public string ZoomInLabel { get; }

    public string ZoomOutLabel { get; }

    public string FitLabel { get; }

    [ObservableProperty]
    public partial string? SelectedLayerId { get; set; }

    [ObservableProperty]
    public partial string ZoomPercent { get; private set; } = "100%";

    public CanvasLayer? SelectedLayer => Layers.FirstOrDefault(layer => layer.Id == SelectedLayerId);

    public bool IsDragging => _drag is not null;

    partial void OnSelectedLayerIdChanged(string? value)
    {
        CommitPendingEdits();
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuilds the canvas model from the open project (including drag previews).</summary>
    public void Refresh()
    {
        ProjectWorkspace? workspace = _projects.Current;
        if (workspace is null)
        {
            Layers = [];
            SelectedLayerId = null;
            InvalidateRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ContentInnerRadiusMm = workspace.Media.InnerRadiusMm;
        ContentOuterRadiusMm = workspace.Media.OuterRadiusMm;
        string? projectPath = _projects.ProjectFilePath;
        Layers = workspace.Design.Layers.Select(layer =>
        {
            string path = ProjectPaths.ResolveAsset(projectPath, layer.ImagePath) ?? layer.ImagePath;
            LayerBitmap? bitmap = _bitmaps.Get(path);
            // Until the image is known the footprint is shown as a square placeholder.
            (double width, double height) = bitmap is { IsMissing: false, SourceWidth: > 0, SourceHeight: > 0 }
                ? ImageComposer.BaseSizeMm(bitmap.SourceWidth, bitmap.SourceHeight, ContentOuterRadiusMm)
                : (2 * ContentOuterRadiusMm, 2 * ContentOuterRadiusMm);
            return new CanvasLayer(
                layer.Id,
                layer.Name,
                layer.Visible,
                layer.Opacity,
                LayerTransform.Of(layer),
                width,
                height,
                bitmap?.Image,
                bitmap?.IsMissing == true,
                bitmap is null);
        }).ToArray();

        if (SelectedLayerId is not null && Layers.All(layer => layer.Id != SelectedLayerId))
        {
            SelectedLayerId = null;
        }

        OnPropertyChanged(nameof(HasLayers));
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets bitmaps and view state when the project closes or another one opens.</summary>
    public void Reset()
    {
        CancelDrag();
        _nudgeTimer.Stop();
        _bitmaps.Clear();
        SelectedLayerId = null;
        Viewport.Fit();
        UpdateZoomPercent();
        Refresh();
    }

    /// <summary>
    /// The layer a click at <paramref name="modelMm"/> means, topmost first by priority: a visible
    /// layer with its image, then a placeholder (missing or still loading, whose footprint is only
    /// an estimate), then a hidden layer. A placeholder never steals a click from real pixels.
    /// </summary>
    public CanvasLayer? HitTest(Point modelMm)
    {
        CanvasLayer? placeholder = null;
        CanvasLayer? hidden = null;
        for (int index = Layers.Count - 1; index >= 0; index--)
        {
            CanvasLayer layer = Layers[index];
            if (!LayerGeometry.Contains(layer.Corners(), modelMm))
            {
                continue;
            }

            if (!layer.Visible)
            {
                hidden ??= layer;
            }
            else if (layer.Image is null)
            {
                placeholder ??= layer;
            }
            else
            {
                return layer;
            }
        }

        return placeholder ?? hidden;
    }

    // ---- View (never persisted) ----

    public void Resize(Size size)
    {
        Viewport.Resize(size);
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomAt(double factor, Point anchor)
    {
        if (Viewport.ZoomAt(factor, anchor))
        {
            UpdateZoomPercent();
            InvalidateRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PanTo(Vector pan)
    {
        Viewport.SetPan(pan);
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ZoomIn() => ZoomBy(CanvasViewport.ButtonZoomStep);

    [RelayCommand]
    private void ZoomOut() => ZoomBy(1 / CanvasViewport.ButtonZoomStep);

    [RelayCommand]
    public void Fit()
    {
        Viewport.Fit();
        UpdateZoomPercent();
        InvalidateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomBy(double factor)
    {
        if (Viewport.ZoomBy(factor))
        {
            UpdateZoomPercent();
            InvalidateRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateZoomPercent()
        => ZoomPercent = string.Format(CultureInfo.CurrentUICulture, "{0:0}%", Viewport.Zoom * 100);

    // ---- Edits (each becomes history through the editor) ----

    /// <summary>Starts a pointer drag of the selected layer; previews until <see cref="CommitDrag"/>.</summary>
    public bool BeginDrag()
    {
        CommitPendingEdits();
        if (SelectedLayer is not { } layer || _drag is not null || !_editor.CanApply)
        {
            return false;
        }

        _drag = _editor.BeginDrag(HistoryLabels.TransformLayer, [layer.Id]);
        _dragLayerId = layer.Id;
        return true;
    }

    public void UpdateDrag(LayerTransform transform)
    {
        if (_drag is null || _dragLayerId is null)
        {
            return;
        }

        string id = _dragLayerId;
        LayerTransform safe = transform with { Scale = Math.Max(MinimumScale, transform.Scale) };
        _drag.Update(workspace => DesignEdits.SetTransform(workspace, id, safe));
    }

    public void CommitDrag()
    {
        ProjectEditor.DragEdit? drag = _drag;
        _drag = null;
        _dragLayerId = null;
        drag?.Commit();
    }

    /// <summary>Restores the state from before the drag (Escape or lost pointer capture).</summary>
    public void CancelDrag()
    {
        ProjectEditor.DragEdit? drag = _drag;
        _drag = null;
        _dragLayerId = null;
        drag?.Cancel();
    }

    /// <summary>
    /// Moves the selected layer by an arrow-key step. Consecutive nudges are one history step,
    /// committed after a short pause or before any other command.
    /// </summary>
    public void Nudge(double dxMm, double dyMm)
    {
        if (SelectedLayer is not { } layer)
        {
            return;
        }

        if (_drag is null)
        {
            if (!_editor.CanApply)
            {
                return;
            }

            _drag = _editor.BeginDrag(HistoryLabels.TransformLayer, [layer.Id]);
            _dragLayerId = layer.Id;
            _nudgeOffset = default;
        }
        else if (_dragLayerId != layer.Id || !_nudgeTimer.IsEnabled)
        {
            return; // a pointer drag is in progress
        }

        _nudgeOffset += new Vector(dxMm, dyMm);
        ProjectWorkspace origin = _drag.Start;
        DesignLayerState original = origin.Design.Layers[DesignEdits.IndexOf(origin.Design.Layers, layer.Id)];
        LayerTransform moved = LayerTransform.Of(original) with
        {
            TranslateXMm = Math.Round(original.TranslateXMm + _nudgeOffset.X, 3),
            TranslateYMm = Math.Round(original.TranslateYMm + _nudgeOffset.Y, 3),
        };
        string id = layer.Id;
        _drag.Update(workspace => DesignEdits.SetTransform(workspace, id, moved));
        _nudgeTimer.Stop();
        _nudgeTimer.Start();
    }

    /// <summary>Commits a pending nudge group; call before undo, save or any other command.</summary>
    public void CommitPendingEdits()
    {
        if (!_nudgeTimer.IsEnabled)
        {
            return;
        }

        _nudgeTimer.Stop();
        CommitDrag();
    }

    public void DeleteSelected()
    {
        CommitPendingEdits();
        if (SelectedLayer is { } layer)
        {
            _editor.Apply(HistoryLabels.RemoveLayers, workspace => DesignEdits.RemoveLayers(workspace, [layer.Id]), [layer.Id]);
        }
    }

    public void DuplicateSelected()
    {
        CommitPendingEdits();
        if (SelectedLayer is not { } layer)
        {
            return;
        }

        string copyName = string.Format(CultureInfo.CurrentUICulture, _text["design.layer.copyName"], layer.Name);
        string? newId = null;
        bool done = _editor.Apply(
            HistoryLabels.DuplicateLayer,
            workspace => DesignEdits.DuplicateLayer(workspace, layer.Id, copyName, out newId)).Succeeded;
        if (done && newId is not null)
        {
            SelectedLayerId = newId;
        }
    }
}
