using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Rendering;

/// <summary>One premultiplied BGRA level of a <see cref="LayerImage"/>.</summary>
public sealed class LayerImageLevel
{
    internal LayerImageLevel(int width, int height, byte[] premultipliedBgra)
    {
        Width = width;
        Height = height;
        Pixels = premultipliedBgra;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Premultiplied BGRA, row-major, no padding.</summary>
    internal byte[] Pixels { get; }
}

/// <summary>
/// A decoded layer ready for sampling: premultiplied BGRA at most
/// <see cref="CompositionLimits.MaximumLayerSide"/> on its longest side, plus a reduction
/// pyramid built on demand so small outputs (thumbnails) are not aliased. Instances are
/// immutable from the outside and safe to share between concurrent compositions.
/// </summary>
public sealed class LayerImage
{
    private readonly List<LayerImageLevel> _levels;
    private readonly Lock _levelsLock = new();

    private LayerImage(LayerImageLevel baseLevel)
    {
        _levels = [baseLevel];
    }

    /// <summary>Width of the full-resolution level; with <see cref="Height"/> it gives the aspect ratio.</summary>
    public int Width => _levels[0].Width;

    public int Height => _levels[0].Height;

    /// <summary>Approximate managed memory held by the levels built so far.</summary>
    public long ByteCount
    {
        get
        {
            lock (_levelsLock)
            {
                return _levels.Sum(level => (long)level.Pixels.Length);
            }
        }
    }

    /// <summary>
    /// Builds a layer image from straight-alpha BGRA, area-averaging it down when its longest
    /// side exceeds <see cref="CompositionLimits.MaximumLayerSide"/>.
    /// </summary>
    public static LayerImage FromStraightBgra(BgraRaster raster, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (raster.Width <= 0 || raster.Height <= 0 || raster.Pixels.Length != (long)raster.Width * raster.Height * 4)
        {
            throw new ArgumentException("The raster dimensions do not match its pixel buffer.", nameof(raster));
        }

        byte[] premultiplied = Premultiply(raster.Pixels, cancellationToken);
        int longest = Math.Max(raster.Width, raster.Height);
        if (longest <= CompositionLimits.MaximumLayerSide)
        {
            return new LayerImage(new LayerImageLevel(raster.Width, raster.Height, premultiplied));
        }

        double factor = (double)CompositionLimits.MaximumLayerSide / longest;
        int width = Math.Max(1, (int)Math.Round(raster.Width * factor, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(raster.Height * factor, MidpointRounding.AwayFromZero));
        byte[] reduced = AreaResampler.Resample(premultiplied, raster.Width, raster.Height, width, height, cancellationToken);
        return new LayerImage(new LayerImageLevel(width, height, reduced));
    }

    /// <summary>Decodes a PNG/JPEG/BMP/GIF file through <see cref="RasterCodec"/> within the composer limits.</summary>
    public static LayerImage Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        BgraRaster raster = RasterCodec.LoadBgra32(
            path,
            CompositionLimits.MaximumSourceFileBytes,
            CompositionLimits.MaximumSourcePixels);
        cancellationToken.ThrowIfCancellationRequested();
        return FromStraightBgra(raster, cancellationToken);
    }

    /// <summary>
    /// Returns the smallest pyramid level whose sides are still at least the requested size,
    /// building missing levels by 2 × 2 premultiplied averaging.
    /// </summary>
    public LayerImageLevel GetLevelFor(double destinationWidth, double destinationHeight, CancellationToken cancellationToken = default)
    {
        LayerImageLevel level = _levels[0];
        while (true)
        {
            int nextWidth = (level.Width + 1) / 2;
            int nextHeight = (level.Height + 1) / 2;
            bool canReduce = level.Width > 1 && level.Height > 1
                             && nextWidth >= destinationWidth && nextHeight >= destinationHeight;
            if (!canReduce)
            {
                return level;
            }

            level = NextLevel(level, cancellationToken);
        }
    }

    private LayerImageLevel NextLevel(LayerImageLevel current, CancellationToken cancellationToken)
    {
        lock (_levelsLock)
        {
            int index = _levels.IndexOf(current);
            if (index >= 0 && index + 1 < _levels.Count)
            {
                return _levels[index + 1];
            }
        }

        LayerImageLevel next = Halve(current, cancellationToken);
        lock (_levelsLock)
        {
            int index = _levels.IndexOf(current);
            if (index + 1 < _levels.Count)
            {
                // Another composition built the same level first; keep the shared instance.
                return _levels[index + 1];
            }

            _levels.Add(next);
            return next;
        }
    }

    private static LayerImageLevel Halve(LayerImageLevel source, CancellationToken cancellationToken)
    {
        int width = (source.Width + 1) / 2;
        int height = (source.Height + 1) / 2;
        byte[] src = source.Pixels;
        byte[] dst = new byte[checked(width * height * 4)];
        int sw = source.Width;
        int sh = source.Height;
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            int y0 = Math.Min(2 * y, sh - 1);
            int y1 = Math.Min(2 * y + 1, sh - 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Min(2 * x, sw - 1);
                int x1 = Math.Min(2 * x + 1, sw - 1);
                int a = ((y0 * sw) + x0) * 4;
                int b = ((y0 * sw) + x1) * 4;
                int c = ((y1 * sw) + x0) * 4;
                int d = ((y1 * sw) + x1) * 4;
                int o = ((y * width) + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    // Sum of four bytes plus 2 rounds the average half away from zero.
                    dst[o + channel] = (byte)((src[a + channel] + src[b + channel] + src[c + channel] + src[d + channel] + 2) >> 2);
                }
            }
        });
        return new LayerImageLevel(width, height, dst);
    }

    private static byte[] Premultiply(byte[] straight, CancellationToken cancellationToken)
    {
        byte[] result = new byte[straight.Length];
        int pixels = straight.Length / 4;
        const int RowBlock = 1 << 16;
        Parallel.For(0, (pixels + RowBlock - 1) / RowBlock, new ParallelOptions { CancellationToken = cancellationToken }, block =>
        {
            int end = Math.Min(pixels, (block + 1) * RowBlock);
            for (int pixel = block * RowBlock; pixel < end; pixel++)
            {
                int i = pixel * 4;
                int alpha = straight[i + 3];
                result[i + 3] = (byte)alpha;
                if (alpha == 255)
                {
                    result[i] = straight[i];
                    result[i + 1] = straight[i + 1];
                    result[i + 2] = straight[i + 2];
                }
                else if (alpha != 0)
                {
                    // round(c × a / 255), half away from zero, in integers.
                    result[i] = (byte)(((straight[i] * alpha) + 127) / 255);
                    result[i + 1] = (byte)(((straight[i + 1] * alpha) + 127) / 255);
                    result[i + 2] = (byte)(((straight[i + 2] * alpha) + 127) / 255);
                }
            }
        });
        return result;
    }
}
