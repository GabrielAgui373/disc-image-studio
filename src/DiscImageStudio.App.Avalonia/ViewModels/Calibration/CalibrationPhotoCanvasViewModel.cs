using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.App.Avalonia.ViewModels.Calibration;

/// <summary>
/// What the calibration photo canvas shows and how it is viewed: the step state, the photo and
/// rectified bitmaps, the portable <see cref="CalibrationPhotoViewport"/> and the handle being
/// edited. Edits go to <see cref="CalibrationPhotoStep"/>; the canvas only draws and routes input.
/// </summary>
public sealed partial class CalibrationPhotoCanvasViewModel : ObservableObject
{
    public const double ZoomStep = 1.25;

    private readonly ITextCatalog _text;

    public CalibrationPhotoCanvasViewModel(CalibrationPhotoStep step, ITextCatalog text)
    {
        Step = step ?? throw new ArgumentNullException(nameof(step));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Step.Changed += (_, _) =>
        {
            if (SelectedEdge is { } edge && SelectedIndex >= Step.Points(edge).Count)
            {
                SelectedEdge = null;
            }

            if (SelectedTraceIndex >= Step.TracePoints.Count)
            {
                SelectedTraceIndex = -1;
            }

            RefreshWorld();
            Invalidate();
        };
        RefreshHint();
    }

    /// <summary>Raised when the canvas must redraw.</summary>
    public event EventHandler? InvalidateRequested;

    public CalibrationPhotoStep Step { get; }

    public CalibrationPhotoViewport Viewport { get; } = new();

    [ObservableProperty]
    public partial Bitmap? PhotoBitmap { get; private set; }

    [ObservableProperty]
    public partial Bitmap? RectifiedBitmap { get; private set; }

    /// <summary>Show the rectified, disc-centred view instead of the photo with its handles.</summary>
    [ObservableProperty]
    public partial bool ShowRectified { get; set; }

    /// <summary>Handles can be moved (photo view, nothing running, not confirmed).</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; set; }

    [ObservableProperty]
    public partial CalibrationEdge? SelectedEdge { get; private set; }

    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Step 3: the rectified view where curve points are placed.</summary>
    [ObservableProperty]
    public partial bool IsTracing { get; set; }

    /// <summary>Curve points can be added, moved and removed.</summary>
    [ObservableProperty]
    public partial bool CanTrace { get; set; }

    /// <summary>Selected curve point (index in radial order), or −1.</summary>
    public int SelectedTraceIndex { get; private set; } = -1;

    /// <summary>The selected candidate's predicted curve, in disc millimetres.</summary>
    public IReadOnlyList<CalibrationPoint>? Prediction { get; private set; }

    /// <summary>Curve points visibly off the prediction (drawn as crosses, not only in another colour).</summary>
    public IReadOnlySet<int> Outliers { get; private set; } = new HashSet<int>();

    /// <summary>Raised when a click or drag is refused (off the disc), with the reason for the hint line.</summary>
    public event EventHandler<string>? TraceRefused;

    [ObservableProperty]
    public partial string ZoomText { get; private set; } = "100%";

    [ObservableProperty]
    public partial string HintText { get; private set; } = string.Empty;

    public string EmptyText => _text["calibration.photo.canvas.empty"];

    public string RectifiedCaption => _text["calibration.photo.canvas.rectified"];

    public string TraceCaption => _text["calibration.curve.canvas.caption"];

    public void ShowHint(string text) => HintText = text;

    public void SetPhotoBitmap(Bitmap? bitmap)
    {
        PhotoBitmap = bitmap;
        RefreshWorld();
        Invalidate();
    }

    public void SetRectifiedBitmap(Bitmap? bitmap)
    {
        RectifiedBitmap = bitmap;
        RefreshWorld();
        Invalidate();
    }

    partial void OnShowRectifiedChanged(bool value)
    {
        SelectedEdge = null;
        SelectedIndex = -1;
        RefreshWorld();
        RefreshHint();
        Invalidate();
    }

    partial void OnIsTracingChanged(bool value)
    {
        SelectedEdge = null;
        SelectedIndex = -1;
        SelectedTraceIndex = -1;
        RefreshWorld();
        RefreshHint();
        Invalidate();
    }

    partial void OnCanTraceChanged(bool value)
    {
        RefreshHint();
        Invalidate();
    }

    public void SetPrediction(IReadOnlyList<CalibrationPoint>? curve, IReadOnlyList<int>? outliers)
    {
        Prediction = curve;
        Outliers = new HashSet<int>(outliers ?? []);
        Invalidate();
    }

    public void SelectTrace(int index)
    {
        SelectedTraceIndex = index;
        RefreshHint();
        Invalidate();
    }

    public bool AddTrace(CalibrationPoint mm)
    {
        if (!CanTrace)
        {
            return false;
        }

        int index = Step.AddTracePoint(mm);
        if (index < 0)
        {
            TraceRefused?.Invoke(this, Step.IsOnDisc(mm) ? _text["calibration.curve.hint.full"] : _text["calibration.curve.hint.offDisc"]);
            return false;
        }

        SelectTrace(index);
        return true;
    }

    public bool MoveTrace(int index, CalibrationPoint mm)
    {
        if (!CanTrace)
        {
            return false;
        }

        int moved = Step.MoveTracePoint(index, mm);
        if (moved < 0)
        {
            TraceRefused?.Invoke(this, _text["calibration.curve.hint.offDisc"]);
            return false;
        }

        SelectTrace(moved);
        return true;
    }

    public bool RemoveSelectedTrace()
    {
        if (!CanTrace || SelectedTraceIndex < 0 || !Step.RemoveTracePoint(SelectedTraceIndex))
        {
            return false;
        }

        SelectTrace(-1);
        return true;
    }

    /// <summary>Moves the selected curve point by millimetres (arrow keys).</summary>
    public bool NudgeSelectedTrace(double dx, double dy)
    {
        if (SelectedTraceIndex < 0 || SelectedTraceIndex >= Step.TracePoints.Count)
        {
            return false;
        }

        CalibrationPoint current = Step.TracePoints[SelectedTraceIndex];
        return MoveTrace(SelectedTraceIndex, new(current.X + dx, current.Y + dy));
    }

    partial void OnCanEditChanged(bool value)
    {
        RefreshHint();
        Invalidate();
    }

    public void Resize(double width, double height, double renderScaling)
    {
        Viewport.Resize(width, height, renderScaling);
        Invalidate();
    }

    public void Select(CalibrationEdge? edge, int index)
    {
        SelectedEdge = edge;
        SelectedIndex = edge is null ? -1 : index;
        RefreshHint();
        Invalidate();
    }

    /// <summary>Commits a handle move to the step (which drops a stale rectification).</summary>
    public bool MoveHandle(CalibrationEdge edge, int index, CalibrationPoint position)
        => CanEdit && Step.MoveHandle(edge, index, position);

    /// <summary>Moves the selected handle by whole working pixels (arrow keys).</summary>
    public bool NudgeSelected(double dx, double dy)
    {
        if (SelectedEdge is not { } edge || SelectedIndex < 0)
        {
            return false;
        }

        CalibrationPoint current = Step.Points(edge)[SelectedIndex];
        return MoveHandle(edge, SelectedIndex, new(current.X + dx, current.Y + dy));
    }

    public void ZoomAt(double factor, CalibrationPoint anchor)
    {
        if (Viewport.ZoomAt(factor, anchor))
        {
            UpdateZoomText();
            Invalidate();
        }
    }

    public void PanBy(double dx, double dy)
    {
        Viewport.PanBy(dx, dy);
        Invalidate();
    }

    [RelayCommand]
    private void ZoomIn() => ZoomAt(ZoomStep, new(Viewport.ViewWidth / 2, Viewport.ViewHeight / 2));

    [RelayCommand]
    private void ZoomOut() => ZoomAt(1 / ZoomStep, new(Viewport.ViewWidth / 2, Viewport.ViewHeight / 2));

    [RelayCommand]
    private void FitView()
    {
        Viewport.Fit();
        UpdateZoomText();
        Invalidate();
    }

    private void RefreshWorld()
    {
        if ((ShowRectified || IsTracing) && Step.Rectification is { } rectification)
        {
            Viewport.ShowDisc(rectification.CanvasRadiusMm);
        }
        else if (Step.Photo is { } photo)
        {
            Viewport.ShowPhoto(photo.Width, photo.Height);
        }

        UpdateZoomText();
    }

    private void UpdateZoomText()
        => ZoomText = string.Format(CultureInfo.CurrentUICulture, "{0:0}%", Viewport.Zoom * 100);

    private void RefreshHint()
        => HintText = IsTracing
            ? !CanTrace
                ? _text["calibration.curve.hint.locked"]
                : SelectedTraceIndex >= 0
                    ? string.Format(CultureInfo.CurrentUICulture, _text["calibration.curve.hint.selected"], SelectedTraceIndex + 1)
                    : _text["calibration.curve.hint.add"]
            : ShowRectified
            ? _text["calibration.photo.hint.rectified"]
            : !CanEdit
                ? _text["calibration.photo.hint.locked"]
                : SelectedEdge switch
                {
                    CalibrationEdge.Outer => _text["calibration.photo.hint.outer"],
                    CalibrationEdge.Hole => _text["calibration.photo.hint.hole"],
                    _ => _text["calibration.photo.hint.edit"],
                };

    private void Invalidate() => InvalidateRequested?.Invoke(this, EventArgs.Empty);
}
