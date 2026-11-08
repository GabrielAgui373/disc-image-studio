using Avalonia;
using DiscImageStudio.Application.Editing;

namespace DiscImageStudio.App.Avalonia.Design;

/// <summary>Footprint math of a layer in model millimetres (no rendering, no state).</summary>
public static class LayerGeometry
{
    /// <summary>
    /// The four corners in the order NW, NE, SE, SW: the base size times scale, rotated clockwise
    /// about the layer centre and translated from the disc centre.
    /// </summary>
    public static Point[] Corners(LayerTransform transform, double baseWidthMm, double baseHeightMm)
    {
        double halfWidth = baseWidthMm * transform.Scale / 2;
        double halfHeight = baseHeightMm * transform.Scale / 2;
        double radians = transform.RotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        (double X, double Y)[] local = [(-halfWidth, -halfHeight), (halfWidth, -halfHeight), (halfWidth, halfHeight), (-halfWidth, halfHeight)];
        return local
            .Select(point => new Point(
                transform.TranslateXMm + (point.X * cos) - (point.Y * sin),
                transform.TranslateYMm + (point.X * sin) + (point.Y * cos)))
            .ToArray();
    }

    public static bool Contains(IReadOnlyList<Point> polygon, Point point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            bool crosses = (polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)
                           && point.X < ((polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Model position of the rotation handle: beyond the midpoint of the (rotated) top edge, at a
    /// fixed screen distance so it stays reachable at any zoom.
    /// </summary>
    public static Point RotateHandle(IReadOnlyList<Point> corners, Point centre, double distanceMm)
    {
        Point topMid = new((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        double dx = topMid.X - centre.X;
        double dy = topMid.Y - centre.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-9)
        {
            (dx, dy, length) = (0, -1, 1);
        }

        return new Point(topMid.X + (dx / length * distanceMm), topMid.Y + (dy / length * distanceMm));
    }

    /// <summary>Clockwise angle in degrees of <paramref name="point"/> around <paramref name="centre"/>.</summary>
    public static double AngleDeg(Point centre, Point point)
        => Math.Atan2(point.Y - centre.Y, point.X - centre.X) * 180.0 / Math.PI;

    /// <summary>Wraps an angle difference into (-180, 180] so crossing the seam never spins the layer.</summary>
    public static double WrapDelta(double delta)
    {
        while (delta <= -180)
        {
            delta += 360;
        }

        while (delta > 180)
        {
            delta -= 360;
        }

        return delta;
    }
}
