using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Rendering;

/// <summary>
/// Reduction of photographs and other opaque rasters. Transparency is composited onto white
/// first, so the exact area average of <see cref="AreaResampler"/> applies to straight pixels.
/// </summary>
public static class RasterReduction
{
    /// <summary>
    /// Composites <paramref name="raster"/> onto white in place and, when its longest side is
    /// above <paramref name="maximumSide"/>, area-averages it down to fit. The caller must not
    /// rely on the input buffer afterwards; it may be returned as the result.
    /// </summary>
    public static BgraRaster FitOpaqueWithin(BgraRaster raster, int maximumSide, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSide);
        if (raster.Width <= 0 || raster.Height <= 0 || raster.Pixels.Length != (long)raster.Width * raster.Height * 4)
        {
            throw new ArgumentException("The raster dimensions do not match its pixel buffer.", nameof(raster));
        }

        FlattenOntoWhite(raster.Pixels, cancellationToken);
        int longest = Math.Max(raster.Width, raster.Height);
        if (longest <= maximumSide)
        {
            return raster;
        }

        (int width, int height) = FittedSize(raster.Width, raster.Height, maximumSide);
        byte[] reduced = AreaResampler.Resample(raster.Pixels, raster.Width, raster.Height, width, height, cancellationToken);
        return new BgraRaster(width, height, reduced);
    }

    /// <summary>The size <see cref="FitOpaqueWithin"/> produces for a source of the given size.</summary>
    public static (int Width, int Height) FittedSize(int width, int height, int maximumSide)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSide);
        int longest = Math.Max(width, height);
        if (longest <= maximumSide)
        {
            return (width, height);
        }

        double factor = (double)maximumSide / longest;
        return (
            Math.Clamp((int)Math.Round(width * factor, MidpointRounding.AwayFromZero), 1, maximumSide),
            Math.Clamp((int)Math.Round(height * factor, MidpointRounding.AwayFromZero), 1, maximumSide));
    }

    private static void FlattenOntoWhite(byte[] pixels, CancellationToken cancellationToken)
    {
        int count = pixels.Length / 4;
        const int Block = 1 << 16;
        Parallel.For(0, (count + Block - 1) / Block, new ParallelOptions { CancellationToken = cancellationToken }, block =>
        {
            int end = Math.Min(count, (block + 1) * Block);
            for (int pixel = block * Block; pixel < end; pixel++)
            {
                int i = pixel * 4;
                int alpha = pixels[i + 3];
                if (alpha == 255)
                {
                    continue;
                }

                // c × a + 255 × (1 − a), rounded half away from zero, in integers.
                int white = 255 * (255 - alpha);
                pixels[i] = (byte)(((pixels[i] * alpha) + white + 127) / 255);
                pixels[i + 1] = (byte)(((pixels[i + 1] * alpha) + white + 127) / 255);
                pixels[i + 2] = (byte)(((pixels[i + 2] * alpha) + white + 127) / 255);
                pixels[i + 3] = 255;
            }
        });
    }
}
