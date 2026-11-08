using DiscImageStudio.Application.Preview;
using DiscImageStudio.Core;

namespace DiscImageStudio.Preview;

/// <summary>
/// Dye colours of one kind of recordable media: what the two data classes of the preview look
/// like on the recorded side. Values are generic approximations, not measurements of a brand.
/// </summary>
/// <param name="Id">Stable id, also the text-catalog key suffix.</param>
/// <param name="Dark">Colour where the preview is black (the denser, darker data class).</param>
/// <param name="Light">Colour where the preview is white.</param>
/// <param name="Unwritten">Dye outside the drawing, where nothing of the design is recorded.</param>
public sealed record MediaAppearance(string Id, OpticalDiscFamily Family, Rgb Dark, Rgb Light, Rgb Unwritten);

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb Hex(uint value) => new((byte)(value >> 16), (byte)(value >> 8), (byte)value);
}

/// <summary>The built-in media looks, per family; the first of each family is its default.</summary>
public static class MediaAppearances
{
    public static IReadOnlyList<MediaAppearance> All { get; } =
    [
        new("cd-gold", OpticalDiscFamily.CompactDisc, Rgb.Hex(0x8A6A2C), Rgb.Hex(0xD9C27E), Rgb.Hex(0xCDB46C)),
        new("cd-blue", OpticalDiscFamily.CompactDisc, Rgb.Hex(0x1F3D6E), Rgb.Hex(0x4F82C2), Rgb.Hex(0x3F6CA8)),
        new("cd-green", OpticalDiscFamily.CompactDisc, Rgb.Hex(0x4F7F6C), Rgb.Hex(0xA4CDBB), Rgb.Hex(0x8DB9A6)),
        new("dvd-purple", OpticalDiscFamily.Dvd, Rgb.Hex(0x40265F), Rgb.Hex(0x8C6CB4), Rgb.Hex(0x7A5AA3)),
        new("dvd-blue", OpticalDiscFamily.Dvd, Rgb.Hex(0x27306A), Rgb.Hex(0x5B70C2), Rgb.Hex(0x4A5DAE)),
        new("bd-gray", OpticalDiscFamily.BluRay, Rgb.Hex(0x3A424C), Rgb.Hex(0x76838F), Rgb.Hex(0x66727E)),
        new("bd-bronze", OpticalDiscFamily.BluRay, Rgb.Hex(0x4A3A2E), Rgb.Hex(0x9A8470), Rgb.Hex(0x86725F)),
    ];

    public static IEnumerable<MediaAppearance> For(OpticalDiscFamily family) => All.Where(media => media.Family == family);

    public static MediaAppearance Default(OpticalDiscFamily family) => For(family).First();
}

/// <summary>
/// Stylized look of the recorded side: the preview's data map recoloured with the media's dye,
/// the low contrast blurred the way the eye merges the marks, the mirror band and clear hub of a
/// 120 mm disc, and a soft iridescent sheen. It suggests the result; it does not predict the
/// reflectance of a particular disc (a photo-calibrated media profile belongs to calibration).
/// </summary>
public static class DiscAppearanceRenderer
{
    // 120 mm disc anatomy, in millimetres: centre hole, clear clamping area, mirror band edge,
    // outer rim.
    private const double HoleRadiusMm = 7.5;
    private const double ClearRadiusMm = 16.5;
    private const double MirrorRadiusMm = 22.5;
    private const double RimRadiusMm = 59.5;

    private static readonly Rgb Clear = Rgb.Hex(0xC9CED6);
    private static readonly Rgb Mirror = Rgb.Hex(0xD8DCE2);

    /// <summary>Opaque-where-disc BGRA of the same size and frame as <paramref name="result"/>.</summary>
    public static byte[] Render(PreviewResult result, MediaAppearance media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(media);
        int size = result.Size;
        byte[] source = result.Pixels;
        PreviewRingOverlay? track = result.Overlays.FirstOrDefault(overlay => overlay.Kind == PreviewOverlayKind.UsefulRing);
        double trackInner = track?.InnerRadiusMm ?? MirrorRadiusMm;
        double trackOuter = track?.OuterRadiusMm ?? RimRadiusMm;

        // Pass 1: data level per pixel (0 dark .. 255 light), -1 where the pixel is not track data.
        short[] level = new short[size * size];
        for (int index = 0; index < level.Length; index++)
        {
            int offset = index * 4;
            level[index] = source[offset + 3] == 0 ? (short)-1 : source[offset + 2];
        }

        // Pass 2: a 3x3 box blur of the data levels, the eye's merging of individual marks.
        short[] smooth = Blur(level, size, cancellationToken);

        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                double radius = result.Frame.RadiusMmAt(x, y);
                if (radius < HoleRadiusMm || radius > RimRadiusMm)
                {
                    continue;
                }

                int index = (y * size) + x;
                Rgb color;
                if (radius < ClearRadiusMm)
                {
                    color = Clear;
                }
                else if (radius < Math.Min(MirrorRadiusMm, trackInner))
                {
                    color = Mirror;
                }
                else if (radius >= trackInner && radius < trackOuter && smooth[index] >= 0)
                {
                    color = Mix(media.Dark, media.Light, smooth[index] / 255.0);
                }
                else
                {
                    color = media.Unwritten;
                }

                double dx = (x + result.Frame.PixelOffset - result.Frame.CenterPx) * result.Frame.MmPerPixel;
                double dy = (y + result.Frame.PixelOffset - result.Frame.CenterPx) * result.Frame.MmPerPixel;
                (double r, double g, double b) = Sheen(color, Math.Atan2(dy, dx), radius);

                // A slightly darker rim reads as the disc edge against any background.
                double edge = radius > RimRadiusMm - 0.6 ? 0.82 : 1.0;
                int offset = index * 4;
                pixels[offset] = ToByte(b * edge);
                pixels[offset + 1] = ToByte(g * edge);
                pixels[offset + 2] = ToByte(r * edge);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static short[] Blur(short[] level, int size, CancellationToken cancellationToken)
    {
        short[] smooth = new short[level.Length];
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                int index = (y * size) + x;
                if (level[index] < 0)
                {
                    smooth[index] = -1;
                    continue;
                }

                int sum = 0;
                int count = 0;
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(size - 1, y + 1); ny++)
                {
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(size - 1, x + 1); nx++)
                    {
                        short value = level[(ny * size) + nx];
                        if (value >= 0)
                        {
                            sum += value;
                            count++;
                        }
                    }
                }

                smooth[index] = (short)(sum / count);
            }
        }

        return smooth;
    }

    /// <summary>
    /// A broad specular band along one diagonal plus a faint rainbow that turns with the angle, as
    /// a disc catching a light above-left of the viewer.
    /// </summary>
    private static (double R, double G, double B) Sheen(Rgb color, double angle, double radiusMm)
    {
        double band = Math.Pow(Math.Max(0, Math.Cos(2 * (angle + (Math.PI / 4)))), 6);
        double highlight = 0.16 * band;
        double hue = (angle * 2) + (radiusMm * 0.08);
        double rainbow = 0.07 * band;
        double r = color.R + ((255 - color.R) * highlight) + (255 * rainbow * (0.5 + (0.5 * Math.Sin(hue))));
        double g = color.G + ((255 - color.G) * highlight) + (255 * rainbow * (0.5 + (0.5 * Math.Sin(hue + 2.094))));
        double b = color.B + ((255 - color.B) * highlight) + (255 * rainbow * (0.5 + (0.5 * Math.Sin(hue + 4.189))));
        return (r, g, b);
    }

    private static Rgb Mix(Rgb dark, Rgb light, double t)
        => new(
            (byte)(dark.R + ((light.R - dark.R) * t)),
            (byte)(dark.G + ((light.G - dark.G) * t)),
            (byte)(dark.B + ((light.B - dark.B) * t)));

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
