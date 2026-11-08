using DiscImageStudio.BluRay;

namespace DiscImageStudio.Preview.BluRay;

/// <summary>
/// Fast nominal BD payload projection, ported from the WPF host's <c>BluRayPreviewRenderer</c>
/// without its bitmap dependency. Black denotes AA (3T bias), white 44 (6T bias) and gray payload
/// outside the drawing range. It is neither an optical reflectance prediction nor an ECC/17PP
/// channel simulation. The tone profile is already applied to the raster by the loader; the only
/// remaining step is the 128 threshold and the optional 3T/6T swap.
/// </summary>
public static class BluRayProjectionRenderer
{
    public const int MinimumSize = 64;
    public const int MaximumSize = 8192;
    private const byte UnknownLevel = 128;

    /// <summary>
    /// Samples <paramref name="source"/> where <paramref name="generated"/> puts each sector and
    /// draws it where <paramref name="actual"/> reads it. Returns an opaque BGRA buffer of
    /// <paramref name="outputSize"/>²; pixels off the track stay transparent black, as in WPF.
    /// </summary>
    public static byte[] RenderCalibration(
        BluRayGray8Raster source,
        BluRayDiscParameters generated,
        BluRayDiscParameters actual,
        uint drawingStartSector,
        uint drawingEndSectorExclusive,
        int outputSize,
        bool invert,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(generated);
        generated.Validate();
        Validate(actual, outputSize);
        if (drawingStartSector >= drawingEndSectorExclusive || drawingEndSectorExclusive > generated.TotalSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(drawingEndSectorExclusive),
                "The drawing range must lie inside the generated disc.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        BluRayTrackGeometry sourceGeometry = new(generated);
        PixelProjection projection = new(actual, outputSize);
        byte[] pixels = new byte[checked(outputSize * outputSize * 4)];
        for (int y = 0; y < outputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outputSize; x++)
            {
                if (!projection.TryGetSector(x, y, out double sector))
                {
                    continue;
                }

                byte level = UnknownLevel;
                if (sector >= drawingStartSector && sector < drawingEndSectorExclusive)
                {
                    BluRayTrackPoint point = sourceGeometry.AtSector(sector);
                    bool dark = source.SampleDiscPoint(point.Xmm, point.Ymm, generated.OuterRadiusMm) < 128;
                    level = dark != invert ? (byte)0 : (byte)255;
                }

                SetPixel(pixels, (y * outputSize) + x, level);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Centre of the output raster in pixels and its scale: pixel (x, y) covers the disc point
    /// <c>((x - centre) * mmPerPixel, (y - centre) * mmPerPixel)</c>, matching
    /// <see cref="PixelProjection"/>.
    /// </summary>
    public static (double CenterPx, double MmPerPixel) Frame(BluRayDiscParameters disc, int outputSize)
        => ((outputSize - 1) / 2.0, disc.OuterRadiusMm * 2 / outputSize);

    private static void Validate(BluRayDiscParameters disc, int size)
    {
        ArgumentNullException.ThrowIfNull(disc);
        disc.Validate();
        if (size is < MinimumSize or > MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "BD preview size must be 64..8192 pixels.");
        }
    }

    private static void SetPixel(byte[] pixels, int pixel, byte level)
    {
        int offset = pixel * 4;
        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = level;
        pixels[offset + 3] = 255;
    }

    /// <summary>Maps an output pixel onto the nearest turn of the actual clockwise
    /// spiral, preserving absolute sector position between generated/measured discs.</summary>
    private sealed class PixelProjection
    {
        private readonly BluRayDiscParameters _disc;
        private readonly int _size;
        private readonly double _mmPerPixel;
        private readonly double _growth;
        private readonly double _innerPrimitive;
        private readonly double _sectorsPerMm;
        private readonly double _angleOffset;

        internal PixelProjection(BluRayDiscParameters disc, int size)
        {
            _disc = disc;
            _size = size;
            _mmPerPixel = disc.OuterRadiusMm * 2 / size;
            BluRayTrackGeometry geometry = new(disc);
            _growth = geometry.EffectiveTrackPitchMicrometers / (2 * Math.PI * 1000);
            _innerPrimitive = Primitive(disc.InnerRadiusMm);
            _sectorsPerMm = disc.TotalSectors / geometry.TrackLengthPerLayerMm;
            _angleOffset = disc.StartAngleDegrees % 360 * Math.PI / 180;
        }

        internal bool TryGetSector(int pixelX, int pixelY, out double sector)
        {
            double x = (pixelX + 0.5 - (_size / 2.0)) * _mmPerPixel;
            double y = ((_size / 2.0) - pixelY - 0.5) * _mmPerPixel;
            double radius = Math.Sqrt((x * x) + (y * y));
            sector = 0;
            if (radius < _disc.InnerRadiusMm || radius >= _disc.OuterRadiusMm)
            {
                return false;
            }

            double spiralAngle = _angleOffset - ((radius - _disc.InnerRadiusMm) / _growth);
            double difference = Math.IEEERemainder(Math.Atan2(y, x) - spiralAngle, 2 * Math.PI);
            double nearestRadius = radius - (difference * _growth);
            if (nearestRadius < _disc.InnerRadiusMm || nearestRadius >= _disc.OuterRadiusMm)
            {
                return false;
            }

            sector = Math.Clamp(
                (Primitive(nearestRadius) - _innerPrimitive) * _sectorsPerMm,
                0,
                Math.BitDecrement((double)_disc.TotalSectors));
            return true;
        }

        private double Primitive(double radius)
            => ((radius * Math.Sqrt((radius * radius) + (_growth * _growth)))
                + (_growth * _growth * Math.Asinh(radius / _growth))) / (2 * _growth);
    }
}
