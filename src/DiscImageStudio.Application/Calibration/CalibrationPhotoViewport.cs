using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>
/// World ↔ view mapping of the calibration photo canvas, free of any UI type so it can be
/// checked on its own. The world is either photo pixels (the working copy, pixel centres at
/// integer coordinates) or disc millimetres (the rectified view, centre at the origin, Y down);
/// the view is device-independent pixels (DIP). <see cref="RenderScaling"/> maps DIP to device
/// pixels, so a handle hit and a drawn line land on the same physical pixel at any DPI.
/// At zoom 1 the world fits the view with a small margin; zoom multiplies that scale and pan
/// moves the world centre away from the view centre.
/// </summary>
public sealed class CalibrationPhotoViewport
{
    public const double MinimumZoom = 0.5;
    public const double MaximumZoom = 40;
    public const double FitFraction = 0.94;

    private double _worldLeft;
    private double _worldTop;
    private double _worldWidth = 1;
    private double _worldHeight = 1;

    public double ViewWidth { get; private set; }

    public double ViewHeight { get; private set; }

    /// <summary>Device pixels per DIP (1 at 96 DPI, 2 on a 2× display).</summary>
    public double RenderScaling { get; private set; } = 1;

    public double Zoom { get; private set; } = 1;

    public double PanX { get; private set; }

    public double PanY { get; private set; }

    /// <summary>DIP per world unit at zoom 1.</summary>
    public double FitScale => ViewWidth <= 0 || ViewHeight <= 0
        ? 0
        : Math.Min(ViewWidth / _worldWidth, ViewHeight / _worldHeight) * FitFraction;

    /// <summary>DIP per world unit.</summary>
    public double Scale => FitScale * Zoom;

    /// <summary>Shows a photo of <paramref name="width"/> × <paramref name="height"/> working pixels.</summary>
    public void ShowPhoto(int width, int height)
        // Pixel centres sit on integers, so the image spans −½ … size − ½.
        => SetWorld(-0.5, -0.5, Math.Max(1, width), Math.Max(1, height));

    /// <summary>Shows a disc-centred square of radius <paramref name="radiusMm"/>.</summary>
    public void ShowDisc(double radiusMm)
    {
        double radius = double.IsFinite(radiusMm) && radiusMm > 0 ? radiusMm : 63;
        SetWorld(-radius, -radius, 2 * radius, 2 * radius);
    }

    public (double Left, double Top, double Width, double Height) World => (_worldLeft, _worldTop, _worldWidth, _worldHeight);

    public void Resize(double width, double height, double renderScaling = 1)
    {
        ViewWidth = Math.Max(0, width);
        ViewHeight = Math.Max(0, height);
        RenderScaling = double.IsFinite(renderScaling) && renderScaling > 0 ? renderScaling : 1;
    }

    public CalibrationPoint WorldToView(CalibrationPoint world)
    {
        double scale = Scale;
        double cx = _worldLeft + (_worldWidth / 2);
        double cy = _worldTop + (_worldHeight / 2);
        return new(((world.X - cx) * scale) + (ViewWidth / 2) + PanX, ((world.Y - cy) * scale) + (ViewHeight / 2) + PanY);
    }

    public CalibrationPoint ViewToWorld(CalibrationPoint view)
    {
        double scale = Scale;
        if (scale <= 0)
        {
            return new(0, 0);
        }

        double cx = _worldLeft + (_worldWidth / 2);
        double cy = _worldTop + (_worldHeight / 2);
        return new(((view.X - (ViewWidth / 2) - PanX) / scale) + cx, ((view.Y - (ViewHeight / 2) - PanY) / scale) + cy);
    }

    public CalibrationPoint ViewToDevice(CalibrationPoint view) => new(view.X * RenderScaling, view.Y * RenderScaling);

    public CalibrationPoint DeviceToView(CalibrationPoint device) => new(device.X / RenderScaling, device.Y / RenderScaling);

    /// <summary>The world rectangle in view coordinates (where the image is drawn).</summary>
    public (double Left, double Top, double Width, double Height) WorldRectInView()
    {
        CalibrationPoint topLeft = WorldToView(new(_worldLeft, _worldTop));
        return (topLeft.X, topLeft.Y, _worldWidth * Scale, _worldHeight * Scale);
    }

    /// <summary>Zooms keeping the world point under <paramref name="anchor"/> (view DIP) in place.</summary>
    public bool ZoomAt(double factor, CalibrationPoint anchor)
    {
        if (!(factor > 0) || !double.IsFinite(factor) || FitScale <= 0)
        {
            return false;
        }

        double zoom = Math.Clamp(Zoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(zoom - Zoom) < 1e-12)
        {
            return false;
        }

        CalibrationPoint world = ViewToWorld(anchor);
        Zoom = zoom;
        CalibrationPoint moved = WorldToView(world);
        PanX += anchor.X - moved.X;
        PanY += anchor.Y - moved.Y;
        return true;
    }

    public bool ZoomBy(double factor) => ZoomAt(factor, new(ViewWidth / 2, ViewHeight / 2));

    public void PanBy(double dx, double dy)
    {
        if (double.IsFinite(dx) && double.IsFinite(dy))
        {
            PanX += dx;
            PanY += dy;
        }
    }

    public void Fit()
    {
        Zoom = 1;
        PanX = 0;
        PanY = 0;
    }

    /// <summary>
    /// The index of the point nearest to <paramref name="view"/> within <paramref name="radiusDip"/>
    /// on screen, or −1. Hits are measured in DIP so a handle is as easy to grab at any zoom.
    /// </summary>
    public int HitTest(IReadOnlyList<CalibrationPoint> worldPoints, CalibrationPoint view, double radiusDip)
    {
        ArgumentNullException.ThrowIfNull(worldPoints);
        int best = -1;
        double bestDistance = radiusDip * radiusDip;
        for (int i = 0; i < worldPoints.Count; i++)
        {
            CalibrationPoint screen = WorldToView(worldPoints[i]);
            double dx = screen.X - view.X;
            double dy = screen.Y - view.Y;
            double distance = (dx * dx) + (dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void SetWorld(double left, double top, double width, double height)
    {
        bool changed = left != _worldLeft || top != _worldTop || width != _worldWidth || height != _worldHeight;
        _worldLeft = left;
        _worldTop = top;
        _worldWidth = width;
        _worldHeight = height;
        if (changed)
        {
            Fit();
        }
    }
}
