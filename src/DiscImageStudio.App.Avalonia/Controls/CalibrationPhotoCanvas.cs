using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiscImageStudio.App.Avalonia.ViewModels.Calibration;
using DiscImageStudio.Application.Calibration;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.App.Avalonia.Controls;

/// <summary>
/// The calibration photo canvas (Task 12.3). Photo view: the working photo with the handles of
/// the outer edge and of the hole, dragged onto the real edges; a loupe magnifies the photo
/// under the handle while it moves. Rectified view: the disc seen from straight above with the
/// physical edges as guides. Coordinates go through the portable
/// <see cref="CalibrationPhotoViewport"/>; Ctrl/Alt + wheel zooms, a drag on empty photo, the
/// middle button or Space + drag pans, arrows nudge the selected handle (Shift: 10 px).
/// </summary>
public sealed class CalibrationPhotoCanvas : Control
{
    private const double HandleHitRadius = 12;
    private const double HandleRadius = 5;
    private const double LoupeSize = 150;
    private const double LoupeZoom = 4;

    public static readonly StyledProperty<CalibrationPhotoCanvasViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<CalibrationPhotoCanvas, CalibrationPhotoCanvasViewModel?>(nameof(ViewModel));

    private bool _panning;
    private bool _spaceHeld;
    private Point _panLast;
    private CalibrationEdge? _dragEdge;
    private int _dragIndex = -1;
    private CalibrationPoint _dragStartWorld = new(0, 0);
    private CalibrationPoint _dragOriginal = new(0, 0);
    private CalibrationPoint _dragPosition = new(0, 0);
    private Point _pointer;
    private int _traceDrag = -1;
    private Point? _lastTraceClick;
    private CalibrationPoint _traceOriginal = new(0, 0);
    private CalibrationPoint _tracePosition = new(0, 0);
    private CalibrationPoint _traceStartWorld = new(0, 0);

    static CalibrationPhotoCanvas()
    {
        FocusableProperty.OverrideDefaultValue<CalibrationPhotoCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<CalibrationPhotoCanvas>(true);
    }

    public CalibrationPhotoCanvasViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private bool Dragging => _dragEdge is not null;

    private bool DraggingTrace => _traceDrag >= 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            if (change.OldValue is CalibrationPhotoCanvasViewModel previous)
            {
                previous.InvalidateRequested -= OnInvalidateRequested;
            }

            if (change.NewValue is CalibrationPhotoCanvasViewModel next)
            {
                next.InvalidateRequested += OnInvalidateRequested;
                Resize(next);
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty && ViewModel is { } model)
        {
            Resize(model);
        }
    }

    private void Resize(CalibrationPhotoCanvasViewModel model)
        => model.Resize(Bounds.Width, Bounds.Height, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private void OnInvalidateRequested(object? sender, EventArgs args)
    {
        if ((Dragging && ViewModel is { CanEdit: false }) || (DraggingTrace && ViewModel is { CanTrace: false }))
        {
            CancelDrag();
        }

        InvalidateVisual();
    }

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2b)), bounds);
        if (ViewModel is not { } model)
        {
            return;
        }

        CalibrationPhotoViewport viewport = model.Viewport;
        if (model.IsTracing)
        {
            if (model.Step.Rectification is null || model.RectifiedBitmap is not { } view)
            {
                DrawCentred(context, model.EmptyText, bounds.Center, Brush("DiscTextMutedBrush", Colors.Gray));
                return;
            }

            DrawBitmap(context, view, viewport);
            DrawRectifiedGuides(context, viewport, model.Step.PhysicalOuterRadiusMm, faint: true);
            DrawTrace(context, model);
            DrawCaption(context, model.TraceCaption);
            if (DraggingTrace)
            {
                DrawLoupe(context, viewport, view, _tracePosition, Brush("DiscAccentBrush", Colors.DeepSkyBlue));
            }

            return;
        }

        if (model.ShowRectified && model.Step.Rectification is not null && model.RectifiedBitmap is { } rectified)
        {
            DrawBitmap(context, rectified, viewport);
            DrawRectifiedGuides(context, viewport, model.Step.PhysicalOuterRadiusMm);
            DrawCaption(context, model.RectifiedCaption);
            return;
        }

        if (model.PhotoBitmap is not { } photo || model.Step.Photo is null)
        {
            DrawCentred(context, model.EmptyText, bounds.Center, Brush("DiscTextMutedBrush", Colors.Gray));
            return;
        }

        DrawBitmap(context, photo, viewport);
        DrawEdges(context, model);
        if (Dragging)
        {
            DrawLoupe(context, viewport, photo, _dragPosition, EdgeBrush(_dragEdge ?? CalibrationEdge.Outer));
        }
    }

    /// <summary>
    /// Curve points (cyan dots joined in radial order), the selected candidate's prediction
    /// (orange) and the points off it, drawn as red crosses so they read without colour.
    /// </summary>
    private void DrawTrace(DrawingContext context, CalibrationPhotoCanvasViewModel model)
    {
        CalibrationPhotoViewport viewport = model.Viewport;
        if (model.Prediction is { Count: > 1 } prediction)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F)), 2), Path(viewport, prediction, closed: false));
        }

        IReadOnlyList<CalibrationPoint> points = model.Step.TracePoints;
        if (DraggingTrace && _traceDrag < points.Count)
        {
            CalibrationPoint[] copy = [.. points];
            copy[_traceDrag] = _tracePosition;
            points = copy;
        }

        IBrush trace = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
        IBrush outlier = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        context.DrawGeometry(null, new Pen(trace, 1.2, new DashStyle([2, 3], 0)), Path(viewport, points, closed: false));
        for (int i = 0; i < points.Count; i++)
        {
            CalibrationPoint view = viewport.WorldToView(points[i]);
            Point centre = new(view.X, view.Y);
            bool selected = i == model.SelectedTraceIndex || i == _traceDrag;
            if (model.Outliers.Contains(i))
            {
                double arm = selected ? 7 : 5;
                Pen cross = new(outlier, 2.4);
                context.DrawLine(new Pen(Brushes.Black, 4.2), new Point(centre.X - arm, centre.Y - arm), new Point(centre.X + arm, centre.Y + arm));
                context.DrawLine(new Pen(Brushes.Black, 4.2), new Point(centre.X - arm, centre.Y + arm), new Point(centre.X + arm, centre.Y - arm));
                context.DrawLine(cross, new Point(centre.X - arm, centre.Y - arm), new Point(centre.X + arm, centre.Y + arm));
                context.DrawLine(cross, new Point(centre.X - arm, centre.Y + arm), new Point(centre.X + arm, centre.Y - arm));
            }
            else
            {
                double radius = selected ? 6 : 4;
                context.DrawEllipse(trace, new Pen(Brushes.Black, 1.2), centre, radius, radius);
            }

            if (selected)
            {
                FormattedText label = Text($"#{i + 1}", 12, Brushes.White);
                Rect box = new(centre.X + 9, centre.Y - label.Height - 8, label.Width + 8, label.Height + 4);
                context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.6), null, new RoundedRect(box, 3));
                context.DrawText(label, new Point(box.Left + 4, box.Top + 2));
            }
        }
    }

    private static void DrawBitmap(DrawingContext context, Bitmap bitmap, CalibrationPhotoViewport viewport)
    {
        (double left, double top, double width, double height) = viewport.WorldRectInView();
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = viewport.Zoom > 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(bitmap, new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height), new Rect(left, top, width, height));
        }
    }

    private void DrawEdges(DrawingContext context, CalibrationPhotoCanvasViewModel model)
    {
        CalibrationPhotoViewport viewport = model.Viewport;
        foreach (CalibrationEdge edge in new[] { CalibrationEdge.Outer, CalibrationEdge.Hole })
        {
            IBrush brush = EdgeBrush(edge);
            IReadOnlyList<CalibrationPoint> points = DisplayPoints(model, edge);
            IReadOnlyList<CalibrationPoint>? fitted = model.Step.Rectification?.FittedEdges.ElementAtOrDefault((int)edge);
            if (fitted is { Count: > 2 } && !Dragging)
            {
                // The fitted ellipse, drawn back on the photo: it should lie on the real edge.
                context.DrawGeometry(null, new Pen(brush, 1.6), Path(viewport, fitted, closed: true));
            }
            else
            {
                context.DrawGeometry(null, new Pen(brush, 1.3, new DashStyle([4, 3], 0)), Path(viewport, points, closed: true));
            }

            if (!model.CanEdit)
            {
                continue;
            }

            for (int i = 0; i < points.Count; i++)
            {
                bool selected = (model.SelectedEdge == edge && model.SelectedIndex == i) || (_dragEdge == edge && _dragIndex == i);
                CalibrationPoint view = viewport.WorldToView(points[i]);
                double radius = selected ? HandleRadius + 2 : HandleRadius;
                context.DrawEllipse(brush, new Pen(Brushes.White, 1.5), new Point(view.X, view.Y), radius, radius);
            }
        }
    }

    /// <summary>A magnified, pixel-exact view of the bitmap under the point being moved.</summary>
    private void DrawLoupe(DrawingContext context, CalibrationPhotoViewport viewport, Bitmap bitmap, CalibrationPoint world, IBrush crossBrush)
    {
        // Opposite corner from the pointer, so the loupe never covers what it shows.
        bool left = _pointer.X > Bounds.Width / 2;
        Rect box = new(left ? 12 : Bounds.Width - LoupeSize - 12, 12, LoupeSize, LoupeSize);
        (double worldLeft, double worldTop, double worldWidth, double worldHeight) = viewport.World;
        double perWorldX = bitmap.PixelSize.Width / worldWidth;
        double perWorldY = bitmap.PixelSize.Height / worldHeight;
        double halfWorld = LoupeSize / 2 / Math.Max(viewport.Scale * LoupeZoom, 2 * perWorldX);
        double cx = (world.X - worldLeft) * perWorldX;
        double cy = (world.Y - worldTop) * perWorldY;
        Rect source = new(cx - (halfWorld * perWorldX), cy - (halfWorld * perWorldY), 2 * halfWorld * perWorldX, 2 * halfWorld * perWorldY);
        using (context.PushClip(new RoundedRect(box, 8)))
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2b)), box);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            {
                context.DrawImage(bitmap, source, box);
            }

            Pen cross = new(crossBrush, 1);
            context.DrawLine(cross, new Point(box.Center.X, box.Top), new Point(box.Center.X, box.Center.Y - 6));
            context.DrawLine(cross, new Point(box.Center.X, box.Center.Y + 6), new Point(box.Center.X, box.Bottom));
            context.DrawLine(cross, new Point(box.Left, box.Center.Y), new Point(box.Center.X - 6, box.Center.Y));
            context.DrawLine(cross, new Point(box.Center.X + 6, box.Center.Y), new Point(box.Right, box.Center.Y));
        }

        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), new RoundedRect(box, 8));
    }

    private void DrawRectifiedGuides(DrawingContext context, CalibrationPhotoViewport viewport, double outerRadiusMm, bool faint = false)
    {
        CalibrationPoint centre = viewport.WorldToView(new(0, 0));
        Point origin = new(centre.X, centre.Y);
        using DrawingContext.PushedState opacity = context.PushOpacity(faint ? 0.45 : 1);
        Pen guide = new(EdgeBrush(CalibrationEdge.Outer), 1.2, new DashStyle([5, 4], 0));
        context.DrawEllipse(null, guide, origin, outerRadiusMm * viewport.Scale, outerRadiusMm * viewport.Scale);
        Pen hole = new(EdgeBrush(CalibrationEdge.Hole), 1.2, new DashStyle([5, 4], 0));
        double holeRadius = CalibrationPhotoLoader.HoleRadiusMm * viewport.Scale;
        context.DrawEllipse(null, hole, origin, holeRadius, holeRadius);
        Pen cross = new(new SolidColorBrush(Colors.White, 0.55), 1);
        double arm = (outerRadiusMm + 2) * viewport.Scale;
        context.DrawLine(cross, new Point(origin.X - arm, origin.Y), new Point(origin.X + arm, origin.Y));
        context.DrawLine(cross, new Point(origin.X, origin.Y - arm), new Point(origin.X, origin.Y + arm));
    }

    private void DrawCaption(DrawingContext context, string text)
    {
        FormattedText label = Text(text, 12, Brushes.White);
        Rect box = new(10, Bounds.Height - label.Height - 18, label.Width + 16, label.Height + 8);
        context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.55), null, new RoundedRect(box, 5));
        context.DrawText(label, new Point(box.Left + 8, box.Top + 4));
    }

    private void DrawCentred(DrawingContext context, string text, Point centre, IBrush brush)
    {
        FormattedText label = Text(text, 14, brush);
        label.MaxTextWidth = Math.Max(120, Bounds.Width - 48);
        label.TextAlignment = TextAlignment.Center;
        context.DrawText(label, new Point(centre.X - (label.MaxTextWidth / 2), centre.Y - (label.Height / 2)));
    }

    private static FormattedText Text(string text, double size, IBrush brush)
        => new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static StreamGeometry Path(CalibrationPhotoViewport viewport, IReadOnlyList<CalibrationPoint> points, bool closed)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext context = geometry.Open();
        if (points.Count > 1)
        {
            CalibrationPoint first = viewport.WorldToView(points[0]);
            context.BeginFigure(new Point(first.X, first.Y), false);
            for (int i = 1; i < points.Count; i++)
            {
                CalibrationPoint view = viewport.WorldToView(points[i]);
                context.LineTo(new Point(view.X, view.Y));
            }

            context.EndFigure(closed);
        }

        return geometry;
    }

    private IReadOnlyList<CalibrationPoint> DisplayPoints(CalibrationPhotoCanvasViewModel model, CalibrationEdge edge)
    {
        IReadOnlyList<CalibrationPoint> points = model.Step.Points(edge);
        if (_dragEdge != edge || _dragIndex < 0 || _dragIndex >= points.Count)
        {
            return points;
        }

        CalibrationPoint[] copy = [.. points];
        copy[_dragIndex] = _dragPosition;
        return copy;
    }

    // Outer edge in the accent colour, hole in amber; both outlined in white on the photo.
    private IBrush EdgeBrush(CalibrationEdge edge)
        => edge == CalibrationEdge.Outer ? Brush("DiscAccentBrush", Colors.DeepSkyBlue) : new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));

    private IBrush Brush(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    // ---- Pointer input ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is not { } model)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
        PointerPoint point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        CalibrationPoint view = new(point.Position.X, point.Position.Y);
        if (model.IsTracing && point.Properties.IsLeftButtonPressed && !_spaceHeld)
        {
            // In the curve step a click adds a point. The second click of a double click on the same
            // spot must not add another one; quick clicks at different places are separate points
            // (the platform counts them as a multi-click by time alone).
            bool repeat = e.ClickCount >= 2 && _lastTraceClick is { } last
                          && Math.Abs(last.X - point.Position.X) < 6 && Math.Abs(last.Y - point.Position.Y) < 6;
            _lastTraceClick = point.Position;
            if (!repeat && model.CanTrace)
            {
                int traceHit = model.Viewport.HitTest(model.Step.TracePoints, view, 10);
                if (traceHit >= 0)
                {
                    _traceDrag = traceHit;
                    _traceOriginal = model.Step.TracePoints[traceHit];
                    _tracePosition = _traceOriginal;
                    _traceStartWorld = model.Viewport.ViewToWorld(view);
                    model.SelectTrace(traceHit);
                    e.Pointer.Capture(this);
                }
                else
                {
                    model.AddTrace(model.Viewport.ViewToWorld(view));
                }
            }

            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2 && HitHandle(model, view) is null)
        {
            model.FitViewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && !_spaceHeld && model.CanEdit && !model.ShowRectified
            && HitHandle(model, view) is { } hit)
        {
            _dragEdge = hit.Edge;
            _dragIndex = hit.Index;
            _dragOriginal = model.Step.Points(hit.Edge)[hit.Index];
            _dragPosition = _dragOriginal;
            _dragStartWorld = model.Viewport.ViewToWorld(view);
            model.Select(hit.Edge, hit.Index);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            // Empty photo, Space or another button: move the view.
            if (point.Properties.IsLeftButtonPressed && !_spaceHeld)
            {
                model.Select(null, -1);
            }

            _panning = true;
            _panLast = point.Position;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ViewModel is not { } model)
        {
            return;
        }

        Point position = e.GetPosition(this);
        _pointer = position;
        if (_panning)
        {
            model.PanBy(position.X - _panLast.X, position.Y - _panLast.Y);
            _panLast = position;
            return;
        }

        if (DraggingTrace)
        {
            CalibrationPoint world = model.Viewport.ViewToWorld(new(position.X, position.Y));
            _tracePosition = new(_traceOriginal.X + world.X - _traceStartWorld.X, _traceOriginal.Y + world.Y - _traceStartWorld.Y);
            InvalidateVisual();
            return;
        }

        if (Dragging)
        {
            CalibrationPoint world = model.Viewport.ViewToWorld(new(position.X, position.Y));
            _dragPosition = new(_dragOriginal.X + world.X - _dragStartWorld.X, _dragOriginal.Y + world.Y - _dragStartWorld.Y);
            if (model.Step.Photo is { } photo)
            {
                _dragPosition = new(Math.Clamp(_dragPosition.X, 0, photo.Width - 1), Math.Clamp(_dragPosition.Y, 0, photo.Height - 1));
            }

            InvalidateVisual();
            return;
        }

        bool overHandle = model.IsTracing
            ? model.CanTrace && model.Viewport.HitTest(model.Step.TracePoints, new(position.X, position.Y), 10) >= 0
            : model.CanEdit && !model.ShowRectified && HitHandle(model, new(position.X, position.Y)) is not null;
        Cursor = new Cursor(overHandle ? StandardCursorType.Cross : _spaceHeld ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            Cursor = new Cursor(StandardCursorType.Arrow);
        }
        else if (DraggingTrace && ViewModel is { } tracing)
        {
            int index = _traceDrag;
            CalibrationPoint position = _tracePosition;
            _traceDrag = -1;
            if (position != _traceOriginal)
            {
                tracing.MoveTrace(index, position);
            }

            InvalidateVisual();
        }
        else if (Dragging && ViewModel is { } model)
        {
            CalibrationEdge edge = _dragEdge!.Value;
            int index = _dragIndex;
            CalibrationPoint position = _dragPosition;
            _dragEdge = null;
            _dragIndex = -1;
            if (position != _dragOriginal)
            {
                model.MoveHandle(edge, index, position);
            }

            model.Select(edge, index);
            InvalidateVisual();
        }

        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false;
        CancelDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // A plain wheel keeps scrolling the page, as on the Design canvas; Ctrl or Alt zoom.
        if (ViewModel is not { } model || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0)
        {
            return;
        }

        Point position = e.GetPosition(this);
        model.ZoomAt(e.Delta.Y > 0 ? 1.15 : 1 / 1.15, new(position.X, position.Y));
        e.Handled = true;
    }

    // ---- Keyboard input ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ViewModel is not { } model)
        {
            return;
        }

        double step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case Key.Space:
                _spaceHeld = true;
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when model.IsTracing:
                e.Handled = model.RemoveSelectedTrace();
                break;
            case Key.Left when model.IsTracing:
                e.Handled = model.NudgeSelectedTrace(-step * 0.02, 0);
                break;
            case Key.Right when model.IsTracing:
                e.Handled = model.NudgeSelectedTrace(step * 0.02, 0);
                break;
            case Key.Up when model.IsTracing:
                e.Handled = model.NudgeSelectedTrace(0, -step * 0.02);
                break;
            case Key.Down when model.IsTracing:
                e.Handled = model.NudgeSelectedTrace(0, step * 0.02);
                break;
            case Key.Escape when Dragging || DraggingTrace:
                CancelDrag();
                e.Handled = true;
                break;
            case Key.Escape:
                model.Select(null, -1);
                e.Handled = true;
                break;
            case Key.Left:
                e.Handled = model.NudgeSelected(-step, 0);
                break;
            case Key.Right:
                e.Handled = model.NudgeSelected(step, 0);
                break;
            case Key.Up:
                e.Handled = model.NudgeSelected(0, -step);
                break;
            case Key.Down:
                e.Handled = model.NudgeSelected(0, step);
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space)
        {
            _spaceHeld = false;
            Cursor = new Cursor(StandardCursorType.Arrow);
            e.Handled = true;
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _spaceHeld = false;
    }

    /// <summary>Keeps the page still when a click focuses the canvas (see DiscDesignCanvas).</summary>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.NavigationMethod != NavigationMethod.Pointer)
        {
            return;
        }

        (ScrollViewer Viewer, Vector Offset)[] scrollers = [.. this.GetVisualAncestors()
            .OfType<ScrollViewer>()
            .Select(viewer => (viewer, viewer.Offset))];
        Dispatcher.UIThread.Post(
            () =>
            {
                foreach ((ScrollViewer viewer, Vector offset) in scrollers)
                {
                    if (viewer.Offset != offset)
                    {
                        viewer.Offset = offset;
                    }
                }
            },
            DispatcherPriority.Send);
    }

    private void CancelDrag()
    {
        if (Dragging || DraggingTrace)
        {
            _dragEdge = null;
            _dragIndex = -1;
            _traceDrag = -1;
            InvalidateVisual();
        }
    }

    private static (CalibrationEdge Edge, int Index)? HitHandle(CalibrationPhotoCanvasViewModel model, CalibrationPoint view)
    {
        // The hole's handles sit on top (they are smaller targets and drawn last).
        foreach (CalibrationEdge edge in new[] { CalibrationEdge.Hole, CalibrationEdge.Outer })
        {
            int index = model.Viewport.HitTest(model.Step.Points(edge), view, HandleHitRadius);
            if (index >= 0)
            {
                return (edge, index);
            }
        }

        return null;
    }
}
