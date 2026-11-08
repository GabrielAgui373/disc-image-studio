using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiscImageStudio.App.Avalonia.Design;
using DiscImageStudio.App.Avalonia.ViewModels.Design;
using DiscImageStudio.Application.Editing;

namespace DiscImageStudio.App.Avalonia.Controls;

/// <summary>
/// Direct-manipulation design canvas. It draws the disc, the content-ring guides, the layers
/// (bottom-first), a mask outside the ring and the selection frame, and turns pointer and
/// keyboard input into <see cref="DesignCanvasViewModel"/> calls. All coordinates go through the
/// view model's <see cref="CanvasViewport"/>; the control keeps only transient gesture state.
/// </summary>
public sealed class DiscDesignCanvas : Control
{
    public const double HandleSize = 8.0;
    public const double RotateHandleDistance = 26.0;
    private const double HubRadiusMm = 15.0;

    public static readonly StyledProperty<DesignCanvasViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<DiscDesignCanvas, DesignCanvasViewModel?>(nameof(ViewModel));

    private Gesture _gesture;
    private Point _gestureStartMm;
    private Point _gestureStartScreen;
    private Vector _panStart;
    private LayerTransform _startTransform;
    private double _startDistanceMm;
    private double _startAngleDeg;

    static DiscDesignCanvas()
    {
        FocusableProperty.OverrideDefaultValue<DiscDesignCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<DiscDesignCanvas>(true);
    }

    private enum Gesture
    {
        None,
        Move,
        Scale,
        Rotate,
        Pan,
    }

    public DesignCanvasViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is DesignCanvasViewModel previous)
            {
                previous.InvalidateRequested -= OnInvalidateRequested;
            }

            if (change.NewValue is DesignCanvasViewModel next)
            {
                next.InvalidateRequested += OnInvalidateRequested;
                next.Resize(Bounds.Size);
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            ViewModel?.Resize(Bounds.Size);
        }
    }

    private void OnInvalidateRequested(object? sender, EventArgs args) => InvalidateVisual();

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(Brush("DiscSurfaceRaisedBrush", Colors.Gainsboro), bounds);
        DesignCanvasViewModel? model = ViewModel;
        if (model is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        CanvasViewport viewport = model.Viewport;
        Point origin = viewport.Origin;
        double discRadius = viewport.ToScreenLength(CanvasViewport.DiscRadiusMm);
        double innerRadius = viewport.ToScreenLength(model.ContentInnerRadiusMm);
        double outerRadius = viewport.ToScreenLength(Math.Min(model.ContentOuterRadiusMm, CanvasViewport.DiscRadiusMm));

        context.DrawEllipse(Brush("DiscSurfaceBrush", Colors.White), new Pen(Brush("DiscBorderBrush", Colors.Gray), 1), origin, discRadius, discRadius);
        double hubRadius = viewport.ToScreenLength(HubRadiusMm);
        context.DrawEllipse(Brush("DiscSurfaceRaisedBrush", Colors.Gainsboro), null, origin, hubRadius, hubRadius);

        foreach (CanvasLayer layer in model.Layers)
        {
            DrawLayer(context, viewport, layer, model.MissingLabel);
        }

        DrawMask(context, bounds, origin, outerRadius, innerRadius);
        IPen guide = new Pen(Brush("DiscAccentBrush", Colors.SteelBlue), 1, new DashStyle([4, 3], 0));
        context.DrawEllipse(null, guide, origin, outerRadius, outerRadius);
        context.DrawEllipse(null, guide, origin, innerRadius, innerRadius);
        IPen cross = new Pen(Brush("DiscTextMutedBrush", Colors.Gray), 1);
        context.DrawLine(cross, origin - new Vector(5, 0), origin + new Vector(5, 0));
        context.DrawLine(cross, origin - new Vector(0, 5), origin + new Vector(0, 5));

        if (model.SelectedLayer is { } selected)
        {
            DrawSelection(context, viewport, selected);
        }

        if (!model.HasLayers)
        {
            DrawCentredText(context, model.EmptyHint, origin, 14, Brush("DiscTextMutedBrush", Colors.Gray));
        }
    }

    private void DrawLayer(DrawingContext context, CanvasViewport viewport, CanvasLayer layer, string missingLabel)
    {
        Point[] corners = layer.Corners().Select(viewport.ToScreen).ToArray();
        if (!layer.Visible)
        {
            // Hidden layers keep a faint rotated outline so they can still be selected.
            context.DrawGeometry(null, new Pen(Brush("DiscTextMutedBrush", Colors.Gray), 1, new DashStyle([3, 3], 0)), Polygon(corners));
            return;
        }

        if (layer.Image is { } image)
        {
            double width = viewport.ToScreenLength(layer.BaseWidthMm * layer.Transform.Scale);
            double height = viewport.ToScreenLength(layer.BaseHeightMm * layer.Transform.Scale);
            Point centre = viewport.ToScreen(layer.Centre);
            Matrix placement = Matrix.CreateRotation(layer.Transform.RotationDeg * Math.PI / 180.0)
                               * Matrix.CreateTranslation(centre.X, centre.Y);
            using (context.PushTransform(placement))
            using (context.PushOpacity(layer.Opacity))
            {
                context.DrawImage(image, new Rect(-width / 2, -height / 2, width, height));
            }

            return;
        }

        // Missing or still loading: keep the footprint findable without inventing pixels.
        IBrush warning = Brush("DiscWarningBrush", Colors.DarkOrange);
        context.DrawGeometry(null, new Pen(warning, 1.5, new DashStyle([5, 3], 0)), Polygon(corners));
        if (layer.IsMissing)
        {
            DrawCentredText(context, missingLabel, viewport.ToScreen(layer.Centre), 12, warning);
        }
    }

    /// <summary>Dims everything outside the content ring with one even-odd geometry.</summary>
    private void DrawMask(DrawingContext context, Rect bounds, Point origin, double outerRadius, double innerRadius)
    {
        GeometryGroup mask = new() { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(bounds));
        mask.Children.Add(new EllipseGeometry(new Rect(origin.X - outerRadius, origin.Y - outerRadius, outerRadius * 2, outerRadius * 2)));
        mask.Children.Add(new EllipseGeometry(new Rect(origin.X - innerRadius, origin.Y - innerRadius, innerRadius * 2, innerRadius * 2)));
        Color backdrop = BrushColor("DiscBackgroundBrush", Colors.White);
        context.DrawGeometry(new SolidColorBrush(backdrop, 0.62), null, mask);
    }

    private void DrawSelection(DrawingContext context, CanvasViewport viewport, CanvasLayer layer)
    {
        IBrush accent = Brush("DiscAccentBrush", Colors.SteelBlue);
        Pen outline = new(accent, 1.5);
        Point[] cornersMm = layer.Corners();
        Point[] corners = cornersMm.Select(viewport.ToScreen).ToArray();
        context.DrawGeometry(null, outline, Polygon(corners));
        foreach (Point corner in corners)
        {
            context.DrawRectangle(Brushes.White, outline, new Rect(corner.X - (HandleSize / 2), corner.Y - (HandleSize / 2), HandleSize, HandleSize));
        }

        Point topMid = new((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        Point handle = viewport.ToScreen(RotateHandleMm(viewport, layer, cornersMm));
        context.DrawLine(outline, topMid, handle);
        context.DrawEllipse(Brushes.White, outline, handle, HandleSize / 2, HandleSize / 2);
    }

    private void DrawCentredText(DrawingContext context, string text, Point centre, double size, IBrush brush)
    {
        FormattedText formatted = new(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            size,
            brush);
        context.DrawText(formatted, new Point(centre.X - (formatted.Width / 2), centre.Y - (formatted.Height / 2)));
    }

    private static StreamGeometry Polygon(IReadOnlyList<Point> points)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext figure = geometry.Open();
        figure.BeginFigure(points[0], isFilled: false);
        for (int index = 1; index < points.Count; index++)
        {
            figure.LineTo(points[index]);
        }

        figure.EndFigure(isClosed: true);
        return geometry;
    }

    private static Point RotateHandleMm(CanvasViewport viewport, CanvasLayer layer, Point[] cornersMm)
        => LayerGeometry.RotateHandle(cornersMm, layer.Centre, RotateHandleDistance / Math.Max(0.001, viewport.Scale));

    private IBrush Brush(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    private Color BrushColor(string key, Color fallback)
        => Brush(key, fallback) is ISolidColorBrush solid ? solid.Color : fallback;

    // ---- Pointer input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        DesignCanvasViewModel? model = ViewModel;
        if (model is null)
        {
            return;
        }

        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        Point screen = point.Position;
        Point mm = model.Viewport.ToModel(screen);

        if (point.Properties.IsMiddleButtonPressed)
        {
            if (model.Viewport.Zoom > 1.0001)
            {
                StartGesture(Gesture.Pan, e, screen, mm);
                _panStart = model.Viewport.Pan;
            }

            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (model.SelectedLayer is { } selected)
        {
            Point[] cornersMm = selected.Corners();
            if (cornersMm.Any(corner => Near(model.Viewport.ToScreen(corner), screen, HandleSize)) && model.BeginDrag())
            {
                StartTransformGesture(Gesture.Scale, e, screen, mm, selected);
                _startDistanceMm = Math.Max(0.001, Distance(mm, selected.Centre));
                return;
            }

            if (Near(model.Viewport.ToScreen(RotateHandleMm(model.Viewport, selected, cornersMm)), screen, HandleSize + 2) && model.BeginDrag())
            {
                StartTransformGesture(Gesture.Rotate, e, screen, mm, selected);
                _startAngleDeg = LayerGeometry.AngleDeg(selected.Centre, mm);
                return;
            }
        }

        if (model.HitTest(mm) is { } hit)
        {
            model.SelectedLayerId = hit.Id;
            if (model.BeginDrag())
            {
                StartTransformGesture(Gesture.Move, e, screen, mm, hit);
            }

            return;
        }

        if (e.ClickCount == 2)
        {
            model.Fit();
            return;
        }

        model.SelectedLayerId = null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        DesignCanvasViewModel? model = ViewModel;
        if (model is null)
        {
            return;
        }

        Point screen = e.GetPosition(this);
        Point mm = model.Viewport.ToModel(screen);
        switch (_gesture)
        {
            case Gesture.Pan:
                model.PanTo(_panStart + (screen - _gestureStartScreen));
                return;
            case Gesture.Move:
                model.UpdateDrag(_startTransform with
                {
                    TranslateXMm = Math.Round(_startTransform.TranslateXMm + (mm.X - _gestureStartMm.X), 3),
                    TranslateYMm = Math.Round(_startTransform.TranslateYMm + (mm.Y - _gestureStartMm.Y), 3),
                });
                return;
            case Gesture.Scale:
                model.UpdateDrag(_startTransform with
                {
                    Scale = Math.Round(Math.Max(DesignCanvasViewModel.MinimumScale, _startTransform.Scale * Distance(mm, Centre(_startTransform)) / _startDistanceMm), 4),
                });
                return;
            case Gesture.Rotate:
            {
                double angle = _startTransform.RotationDeg + LayerGeometry.WrapDelta(LayerGeometry.AngleDeg(Centre(_startTransform), mm) - _startAngleDeg);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    // Snap to the nearest right angle; a full turn reads as 0.
                    angle = Math.Round(angle / 90.0) * 90.0 % 360;
                }

                model.UpdateDrag(_startTransform with { RotationDeg = Math.Round(angle, 1) });
                return;
            }
        }

        UpdateCursor(model, screen);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
        {
            ViewModel?.CommitDrag();
        }

        EndGesture(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
        {
            // Losing the pointer mid-drag (another window, a dialog) restores the start state.
            ViewModel?.CancelDrag();
        }

        _gesture = Gesture.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // A plain wheel keeps scrolling the page; Ctrl or Alt zoom around the pointer.
        if (ViewModel is null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0)
        {
            return;
        }

        ViewModel.ZoomAt(e.Delta.Y > 0 ? CanvasViewport.WheelZoomStep : 1 / CanvasViewport.WheelZoomStep, e.GetPosition(this));
        e.Handled = true;
    }

    // ---- Keyboard input ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        DesignCanvasViewModel? model = ViewModel;
        if (model is null || e.Handled)
        {
            return;
        }

        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DesignCanvasViewModel.LargeNudgeMm : DesignCanvasViewModel.NudgeMm;
        switch (e.Key)
        {
            case Key.Left:
                model.Nudge(-step, 0);
                break;
            case Key.Right:
                model.Nudge(step, 0);
                break;
            case Key.Up:
                model.Nudge(0, -step);
                break;
            case Key.Down:
                model.Nudge(0, step);
                break;
            case Key.Delete:
            case Key.Back:
                model.DeleteSelected();
                break;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                model.DuplicateSelected();
                break;
            case Key.Escape:
                if (model.IsDragging && _gesture == Gesture.None)
                {
                    model.CommitPendingEdits();
                }
                else if (_gesture is Gesture.Move or Gesture.Scale or Gesture.Rotate)
                {
                    model.CancelDrag();
                    _gesture = Gesture.None;
                }
                else
                {
                    model.SelectedLayerId = null;
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- Gesture helpers ----

    private void StartGesture(Gesture gesture, PointerPressedEventArgs e, Point screen, Point mm)
    {
        _gesture = gesture;
        _gestureStartScreen = screen;
        _gestureStartMm = mm;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void StartTransformGesture(Gesture gesture, PointerPressedEventArgs e, Point screen, Point mm, CanvasLayer layer)
    {
        _startTransform = layer.Transform;
        StartGesture(gesture, e, screen, mm);
    }

    private void EndGesture(IPointer pointer)
    {
        _gesture = Gesture.None;
        if (pointer.Captured == this)
        {
            pointer.Capture(null);
        }
    }

    private void UpdateCursor(DesignCanvasViewModel model, Point screen)
    {
        StandardCursorType cursor = StandardCursorType.Arrow;
        if (model.SelectedLayer is { } selected)
        {
            Point[] cornersMm = selected.Corners();
            if (cornersMm.Any(corner => Near(model.Viewport.ToScreen(corner), screen, HandleSize))
                || Near(model.Viewport.ToScreen(RotateHandleMm(model.Viewport, selected, cornersMm)), screen, HandleSize + 2))
            {
                cursor = StandardCursorType.SizeAll;
            }
        }

        if (cursor == StandardCursorType.Arrow && model.HitTest(model.Viewport.ToModel(screen)) is not null)
        {
            cursor = StandardCursorType.Hand;
        }

        Cursor = new Cursor(cursor);
    }

    private static Point Centre(LayerTransform transform) => new(transform.TranslateXMm, transform.TranslateYMm);

    private static bool Near(Point a, Point b, double tolerance)
        => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private static double Distance(Point a, Point b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
