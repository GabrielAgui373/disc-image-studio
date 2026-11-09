using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.Application.Calibration;

/// <summary>A calibration failure with a stable code the presentation layer localizes.</summary>
public sealed class CalibrationException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The working copy of a disc photograph: opaque straight BGRA, reduced so its longest side is
/// at most the chosen limit. Boundary points, the rectification homography and the session file
/// are all in this image's pixel coordinates (as in the WPF wizard); the affine map back to the
/// original file's pixels is kept so nothing measured depends on the reduction being undone.
/// </summary>
public sealed class CalibrationPhoto
{
    internal CalibrationPhoto(BgraRaster raster, int originalWidth, int originalHeight, bool fromSession)
    {
        Raster = raster;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        FromSession = fromSession;
    }

    /// <summary>The working pixels; callers must not modify them.</summary>
    public BgraRaster Raster { get; }

    public int Width => Raster.Width;

    public int Height => Raster.Height;

    /// <summary>Pixel size of the decoded file; equal to the working size for a session photo.</summary>
    public int OriginalWidth { get; }

    public int OriginalHeight { get; }

    /// <summary>The photo came from a session file, where only the reduced copy is stored.</summary>
    public bool FromSession { get; }

    public bool IsReduced => Width != OriginalWidth || Height != OriginalHeight;

    /// <summary>
    /// Row-major 3 × 3 affine map from working pixel coordinates to original pixel coordinates.
    /// Area reduction maps pixel centres, so x<sub>o</sub> = (x + ½)·W<sub>o</sub>/W − ½.
    /// </summary>
    public double[] WorkingToOriginalMatrix9
    {
        get
        {
            double sx = (double)OriginalWidth / Width;
            double sy = (double)OriginalHeight / Height;
            return [sx, 0, (0.5 * sx) - 0.5, 0, sy, (0.5 * sy) - 0.5, 0, 0, 1];
        }
    }

    public CalibrationPoint ToOriginal(CalibrationPoint working)
    {
        ArgumentNullException.ThrowIfNull(working);
        double sx = (double)OriginalWidth / Width;
        double sy = (double)OriginalHeight / Height;
        return new(((working.X + 0.5) * sx) - 0.5, ((working.Y + 0.5) * sy) - 0.5);
    }

    public CalibrationPoint FromOriginal(CalibrationPoint original)
    {
        ArgumentNullException.ThrowIfNull(original);
        double sx = (double)Width / OriginalWidth;
        double sy = (double)Height / OriginalHeight;
        return new(((original.X + 0.5) * sx) - 0.5, ((original.Y + 0.5) * sy) - 0.5);
    }

    /// <summary>True when the point lies on the working image; same bound as the WPF session reader.</summary>
    public bool Contains(CalibrationPoint point)
        => point is not null && point.X >= 0 && point.Y >= 0 && point.X < Width && point.Y < Height;

    /// <summary>The working copy as base64 PNG, the form a <see cref="CalibrationPhotoSession"/> embeds.</summary>
    public string ToSessionPng() => Convert.ToBase64String(RasterCodec.EncodePng(Width, Height, Raster.Pixels, grayscale: false));
}

/// <summary>Suggested starting contours for the outer edge and the centre hole, in working pixels.</summary>
public sealed record CalibrationPhotoContours(IReadOnlyList<CalibrationPoint> Outer, IReadOnlyList<CalibrationPoint> Hole);

/// <summary>
/// Bounded photo decoding, reduction and perspective resampling (port of the WPF
/// <c>CalibrationPhotoService</c>). Independent of the engraved target.
/// </summary>
public static class CalibrationPhotoLoader
{
    /// <summary>The WPF wizard's working size.</summary>
    public const int DefaultMaximumSide = 1600;

    public const int SmallestMaximumSide = 64;

    /// <summary>Session files reject boundary points beyond 2000 px, so no working copy is larger.</summary>
    public const int LargestMaximumSide = 2000;

    /// <summary>Smallest photo side a session accepts.</summary>
    public const int MinimumSide = 32;

    public const long MaximumFileBytes = CompositionLimits.MaximumSourceFileBytes;

    /// <summary>Largest decoded photo; the full-size decode is the peak of the load.</summary>
    public const long MaximumSourcePixels = CompositionLimits.MaximumSourcePixels;

    /// <summary>Physical radius of the centre hole every disc shares.</summary>
    public const double HoleRadiusMm = 7.5;

    /// <summary>
    /// Decodes the photo, composites any transparency onto white and area-averages it so its
    /// longest side is at most <paramref name="maximumSide"/>. The full-size pixels are released
    /// before returning. Size limits are checked from the header before decoding.
    /// </summary>
    public static CalibrationPhoto Load(string path, int maximumSide = DefaultMaximumSide, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumSide is < SmallestMaximumSide or > LargestMaximumSide)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSide), $"The working size must be {SmallestMaximumSide}–{LargestMaximumSide} px.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        (int width, int height) = ReadHeader(path);
        if (width < MinimumSide || height < MinimumSide)
        {
            throw new CalibrationException("calibration.photo.tooSmall", $"The photo must be at least {MinimumSide} px on each side.");
        }

        BgraRaster decoded;
        try
        {
            decoded = RasterCodec.LoadBgra32(path, MaximumFileBytes, MaximumSourcePixels);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new CalibrationException("calibration.photo.unreadable", "The photo could not be decoded.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        BgraRaster working = RasterReduction.FitOpaqueWithin(decoded, maximumSide, cancellationToken);
        return new CalibrationPhoto(working, decoded.Width, decoded.Height, fromSession: false);
    }

    /// <summary>The working size <see cref="Load"/> would produce, read from the header only.</summary>
    public static (int Width, int Height) PlannedSize(string path, int maximumSide = DefaultMaximumSide)
    {
        (int width, int height) = ReadHeader(path);
        return RasterReduction.FittedSize(width, height, maximumSide);
    }

    /// <summary>Restores the working copy embedded in a session (WPF sessions included).</summary>
    public static CalibrationPhoto FromSession(CalibrationPhotoSession photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        BgraRaster raster;
        try
        {
            byte[] png = Convert.FromBase64String(photo.ImagePngBase64);
            raster = RasterCodec.DecodeBgra32(png, (long)LargestMaximumSide * LargestMaximumSide);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new CalibrationException("calibration.session.photoUnreadable", "The photo stored in the session could not be decoded.", exception);
        }

        if (raster.Width is < MinimumSide or > LargestMaximumSide || raster.Height is < MinimumSide or > LargestMaximumSide)
        {
            throw new CalibrationException("calibration.session.photoSize", "The photo stored in the session has an invalid size.");
        }

        raster = RasterReduction.FitOpaqueWithin(raster, LargestMaximumSide);
        CalibrationPhoto restored = new(raster, raster.Width, raster.Height, fromSession: true);
        if (photo.OuterBoundary.Concat(photo.HoleBoundary).Any(point => !restored.Contains(point)))
        {
            throw new CalibrationException("calibration.session.boundaryOutside", "The session's contours lie outside its photo.");
        }

        return restored;
    }

    /// <summary>Evenly spaced starting points for both contours, centred on the photo.</summary>
    public static CalibrationPhotoContours SuggestContours(CalibrationPhoto photo, int count = 8)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        double cx = (photo.Width - 1) / 2.0;
        double cy = (photo.Height - 1) / 2.0;
        double rx = photo.Width * 0.43;
        double ry = photo.Height * 0.43;
        CalibrationPoint[] Ring(double ratio) => Enumerable.Range(0, count).Select(index =>
        {
            double angle = (-Math.PI / 2) + (2 * Math.PI * index / count);
            return new CalibrationPoint(cx + (rx * ratio * Math.Cos(angle)), cy + (ry * ratio * Math.Sin(angle)));
        }).ToArray();
        return new(Array.AsReadOnly(Ring(1)), Array.AsReadOnly(Ring(0.125)));
    }

    /// <summary>
    /// Resamples the photo into a square, disc-centred view of radius
    /// <paramref name="canvasOuterRadiusMm"/> (Y down), bilinear, white outside the disc or the
    /// photo. Same arithmetic as the WPF wizard.
    /// </summary>
    public static BgraRaster Rectify(
        CalibrationPhoto photo,
        CalibrationPhotoTransform transform,
        double canvasOuterRadiusMm,
        int pixelSize = 1400,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(transform);
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(canvasOuterRadiusMm) || canvasOuterRadiusMm is <= 0 or > 150)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasOuterRadiusMm));
        }

        if (pixelSize is < 32 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize));
        }

        if (!transform.IsValidForDisc(canvasOuterRadiusMm))
        {
            throw new CalibrationException("calibration.photo.degenerate", "The photo transform degenerates inside the canvas.");
        }

        int width = photo.Width;
        int height = photo.Height;
        int sourceStride = width * 4;
        int stride = checked(pixelSize * 4);
        byte[] source = photo.Raster.Pixels;
        byte[] pixels = new byte[checked(stride * pixelSize)];
        double[] matrix = transform.DiscToSourceMatrix9;
        double step = 2 * canvasOuterRadiusMm / pixelSize;
        double limit2 = canvasOuterRadiusMm * canvasOuterRadiusMm;
        Parallel.For(0, pixelSize, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double ym = ((y + 0.5) * step) - canvasOuterRadiusMm;
            for (int x = 0; x < pixelSize; x++)
            {
                double xm = ((x + 0.5) * step) - canvasOuterRadiusMm;
                double divisor = (matrix[6] * xm) + (matrix[7] * ym) + matrix[8];
                int destination = (y * stride) + (x * 4);
                double sx = ((matrix[0] * xm) + (matrix[1] * ym) + matrix[2]) / divisor;
                double sy = ((matrix[3] * xm) + (matrix[4] * ym) + matrix[5]) / divisor;
                if ((xm * xm) + (ym * ym) > limit2
                    || !double.IsFinite(sx) || !double.IsFinite(sy)
                    || sx < 0 || sy < 0 || sx > width - 1 || sy > height - 1)
                {
                    pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = pixels[destination + 3] = 255;
                    continue;
                }

                int x0 = (int)Math.Floor(sx);
                int y0 = (int)Math.Floor(sy);
                int x1 = Math.Min(x0 + 1, width - 1);
                int y1 = Math.Min(y0 + 1, height - 1);
                double fx = sx - x0;
                double fy = sy - y0;
                for (int channel = 0; channel < 4; channel++)
                {
                    double value = (source[(y0 * sourceStride) + (x0 * 4) + channel] * (1 - fx) * (1 - fy))
                                   + (source[(y0 * sourceStride) + (x1 * 4) + channel] * fx * (1 - fy))
                                   + (source[(y1 * sourceStride) + (x0 * 4) + channel] * (1 - fx) * fy)
                                   + (source[(y1 * sourceStride) + (x1 * 4) + channel] * fx * fy);
                    pixels[destination + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        return new BgraRaster(pixelSize, pixelSize, pixels);
    }

    /// <summary>
    /// The fitted outer edge and hole drawn back on the photo (working pixels), for checking the
    /// rectification against the real edges.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CalibrationPoint>> BoundaryContours(
        CalibrationPhotoTransform transform,
        double physicalOuterRadiusMm,
        int samples = 180)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 8);
        return new[] { physicalOuterRadiusMm, HoleRadiusMm }.Select(radius => (IReadOnlyList<CalibrationPoint>)Enumerable
            .Range(0, samples + 1)
            .Select(i =>
            {
                double angle = i * 2 * Math.PI / samples;
                return transform.DiscToSource(new(radius * Math.Cos(angle), radius * Math.Sin(angle)));
            })
            .ToArray()).ToArray();
    }

    private static (int Width, int Height) ReadHeader(string path)
    {
        try
        {
            return RasterCodec.ReadSize(path, MaximumFileBytes, MaximumSourcePixels);
        }
        catch (FileNotFoundException exception)
        {
            throw new CalibrationException("calibration.photo.notFound", "The photo file does not exist.", exception);
        }
        catch (ArgumentException exception) when (exception.ParamName == "path")
        {
            throw new CalibrationException("calibration.photo.tooLarge", exception.Message, exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new CalibrationException("calibration.photo.unreadable", "The photo could not be decoded.", exception);
        }
    }
}
