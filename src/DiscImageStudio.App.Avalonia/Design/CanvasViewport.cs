using Avalonia;

namespace DiscImageStudio.App.Avalonia.Design;

/// <summary>
/// Explicit model ↔ screen mapping of the design canvas. The model is millimetres with the
/// origin at the disc centre (x right, y down); the screen is device-independent pixels (DIP),
/// so the platform's render scaling handles physical DPI. At zoom 1 the whole disc plus a small
/// margin fits the control; zoom multiplies that fit scale and pan offsets the disc centre.
/// </summary>
public sealed class CanvasViewport
{
    /// <summary>Physical radius of a 120 mm disc.</summary>
    public const double DiscRadiusMm = 60.0;

    /// <summary>Margin around the disc at fit zoom, as in the WPF canvas.</summary>
    public const double ViewMarginMm = 3.0;

    public const double MinimumZoom = 0.25;
    public const double MaximumZoom = 12.0;
    public const double ButtonZoomStep = 1.25;
    public const double WheelZoomStep = 1.15;

    public Size ViewSize { get; private set; }

    /// <summary>Multiplier on top of the fit scale; 1 shows the whole disc.</summary>
    public double Zoom { get; private set; } = 1.0;

    /// <summary>Offset of the disc centre from the view centre, in DIP. Zero at or below fit.</summary>
    public Vector Pan { get; private set; }

    /// <summary>DIP per millimetre at zoom 1.</summary>
    public double FitScale
    {
        get
        {
            double side = 2 * (DiscRadiusMm + ViewMarginMm);
            return ViewSize.Width <= 0 || ViewSize.Height <= 0
                ? 0
                : Math.Min(ViewSize.Width / side, ViewSize.Height / side);
        }
    }

    /// <summary>DIP per millimetre at the current zoom.</summary>
    public double Scale => FitScale * Zoom;

    /// <summary>Screen position of the disc centre (model origin).</summary>
    public Point Origin => new((ViewSize.Width / 2) + Pan.X, (ViewSize.Height / 2) + Pan.Y);

    /// <summary>The model → screen transform as one matrix: scale, then translate to <see cref="Origin"/>.</summary>
    public Matrix ModelToScreen => Matrix.CreateScale(Scale, Scale) * Matrix.CreateTranslation(Origin.X, Origin.Y);

    public Point ToScreen(Point modelMm) => new(Origin.X + (modelMm.X * Scale), Origin.Y + (modelMm.Y * Scale));

    public Point ToModel(Point screen)
    {
        double scale = Scale;
        return scale <= 0
            ? default
            : new Point((screen.X - Origin.X) / scale, (screen.Y - Origin.Y) / scale);
    }

    public double ToScreenLength(double millimetres) => millimetres * Scale;

    public void Resize(Size size)
    {
        ViewSize = size;
        Pan = ClampPan(Pan);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/> keeping the model point under <paramref name="anchor"/>
    /// on the same screen pixel. At or below fit the disc is centred again, because panning a
    /// shrunken disc away from the middle is never useful.
    /// </summary>
    public bool ZoomAt(double factor, Point anchor)
    {
        double fit = FitScale;
        if (fit <= 0 || !(factor > 0))
        {
            return false;
        }

        Point anchorMm = ToModel(anchor);
        double zoom = Math.Clamp(Zoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(zoom - Zoom) < 1e-9)
        {
            return false;
        }

        Zoom = zoom;
        Vector viewCentre = new(ViewSize.Width / 2, ViewSize.Height / 2);
        Pan = Zoom > 1.0001
            ? ClampPan(new Vector(anchor.X, anchor.Y) - viewCentre - (new Vector(anchorMm.X, anchorMm.Y) * (fit * Zoom)))
            : default;
        return true;
    }

    /// <summary>Zooms around the view centre (toolbar buttons).</summary>
    public bool ZoomBy(double factor) => ZoomAt(factor, new Point(ViewSize.Width / 2, ViewSize.Height / 2) + Pan);

    public void PanBy(Vector delta) => Pan = ClampPan(Pan + delta);

    public void SetPan(Vector pan) => Pan = ClampPan(pan);

    public void Fit()
    {
        Zoom = 1.0;
        Pan = default;
    }

    /// <summary>The disc centre stays within the disc's on-screen radius, so the artwork can never be lost.</summary>
    private Vector ClampPan(Vector pan)
    {
        double fit = FitScale;
        if (fit <= 0 || Zoom <= 1.0001)
        {
            return default;
        }

        double maximum = fit * Zoom * DiscRadiusMm;
        double length = Math.Sqrt((pan.X * pan.X) + (pan.Y * pan.Y));
        return length > maximum ? pan * (maximum / length) : pan;
    }
}
