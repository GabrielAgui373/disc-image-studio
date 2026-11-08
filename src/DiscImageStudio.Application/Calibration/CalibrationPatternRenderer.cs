using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Application.Calibration;

/// <summary>A row-major 8-bit grey raster.</summary>
public sealed record GrayRaster(int Width, int Height, byte[] Pixels)
{
    public byte[] EncodePng() => RasterCodec.EncodePng(Width, Height, Pixels, grayscale: true);
}

/// <summary>
/// Portable renderer of the calibration pattern (port of the WPF
/// <c>CalibrationPatternRenderer</c>, without <c>BitmapSource</c>). The pixels are the same:
/// the target owns its sampling, and both images are the binary values the encoders write.
/// </summary>
public static class CalibrationPatternRenderer
{
    /// <summary>Largest side accepted for a disc-view render.</summary>
    public const int MaximumViewSize = 4096;

    /// <summary>
    /// The target as it lands on a disc of radius <paramref name="canvasRadiusMm"/>, seen from
    /// above, at <paramref name="size"/> × <paramref name="size"/> pixels. With
    /// <paramref name="actual"/> the pattern is drawn as those measured parameters would place
    /// it (for comparing a candidate with the photo); white outside the drawing annulus.
    /// </summary>
    public static GrayRaster RenderDiscView(
        CalibrationTarget target,
        double canvasRadiusMm,
        int size,
        CalibrationParameters? actual = null,
        double rotationRadians = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!double.IsFinite(canvasRadiusMm) || canvasRadiusMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasRadiusMm));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaximumViewSize);
        Func<CalibrationPoint, CalibrationPoint?>? inverse = actual is null
            ? null
            : target.CreateInverseMapping(actual, rotationRadians);
        CalibrationParameters bounds = actual ?? target.Parameters;
        double inner2 = bounds.InnerRadiusMm * bounds.InnerRadiusMm;
        double outer2 = bounds.OuterRadiusMm * bounds.OuterRadiusMm;

        // The canvas radius sets the physical background only; the target's own parameters set
        // the scale of the reference pixels.
        byte[] pixels = new byte[checked(size * size)];
        double step = 2 * canvasRadiusMm / size;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = ((y + 0.5) * step) - canvasRadiusMm;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                double xm = ((x + 0.5) * step) - canvasRadiusMm;
                double r2 = (xm * xm) + (ym * ym);
                byte value = 255;
                if (r2 >= inner2 - 1e-10 && r2 <= outer2 + 1e-10)
                {
                    CalibrationPoint? point = inverse is null ? new(xm, ym) : inverse(new(xm, ym));
                    if (point is not null)
                    {
                        value = BinaryGrayscale.Quantize(target.Sample(point.X, point.Y));
                    }
                }

                pixels[row + x] = value;
            }
        });
        return new GrayRaster(size, size, pixels);
    }

    /// <summary>
    /// The 2400 × 2400 reference pixels in the ordinary encoder's image coordinates, white
    /// outside the target's drawing annulus: the PNG a user can feed to the normal pipeline.
    /// </summary>
    public static GrayRaster RenderEncodingImage(CalibrationTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        const int Size = CalibrationReferencePattern.PixelSize;
        byte[] pixels = CalibrationReferencePattern.ReadGrayPixels();
        CalibrationParameters parameters = target.Parameters;
        double inner2 = target.DrawingInnerRadiusMm * target.DrawingInnerRadiusMm;
        double outer2 = target.DrawingOuterRadiusMm * target.DrawingOuterRadiusMm;
        Parallel.For(0, Size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < Size; x++)
            {
                CalibrationPoint p = CalibrationReferencePattern.PixelToDisc(x, y, parameters.OuterRadiusMm, parameters.Kind);
                double r2 = (p.X * p.X) + (p.Y * p.Y);
                if (r2 < inner2 - 1e-10 || r2 > outer2 + 1e-10)
                {
                    pixels[(y * Size) + x] = 255;
                }
            }
        });
        return new GrayRaster(Size, Size, pixels);
    }
}
