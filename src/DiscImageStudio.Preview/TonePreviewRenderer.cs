using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Preview;

/// <summary>
/// Before/after of the tone profile on the composed design (ADR 3 of the preview docs): the
/// same portable functions generation applies (<see cref="ToneProfile.UseWhite"/>, and
/// <see cref="CdToneMapping"/> with its four-level palette for CD), on a small copy of the
/// composition. The grey conversion follows each family: CD weighs RGB with Rec. 601 and ignores
/// alpha, DVD and Blu-ray use Rec. 709 over white. Dither phases are indexed by output pixel, so
/// the grain is indicative; the density of black and white is what generation produces.
/// </summary>
public static class TonePreviewRenderer
{
    /// <summary>
    /// Nearest-neighbour copy of <paramref name="source"/> at <paramref name="size"/>² (before)
    /// and the same pixels through the tone (after), both opaque BGRA.
    /// </summary>
    public static (byte[] Before, byte[] After) Render(
        BgraRaster source,
        int size,
        OpticalDiscFamily family,
        ToneProfile tone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ToneProfile normalized = tone.Normalized();
        bool cd = family == OpticalDiscFamily.CompactDisc;
        if (!cd)
        {
            normalized = normalized with { Levels = ToneProfile.TwoLevels };
        }

        byte[] before = new byte[checked(size * size * 4)];
        byte[] after = new byte[before.Length];
        byte[] pixels = source.Pixels;
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceY = Math.Min(source.Height - 1, (int)((y + 0.5) * source.Height / size));
            for (int x = 0; x < size; x++)
            {
                int sourceX = Math.Min(source.Width - 1, (int)((x + 0.5) * source.Width / size));
                int from = ((sourceY * source.Width) + sourceX) * 4;
                int to = ((y * size) + x) * 4;
                byte blue = pixels[from], green = pixels[from + 1], red = pixels[from + 2], alpha = pixels[from + 3];

                // Before: the composition over white, as it would print.
                before[to] = OverWhite(blue, alpha);
                before[to + 1] = OverWhite(green, alpha);
                before[to + 2] = OverWhite(red, alpha);
                before[to + 3] = 255;

                long index = (long)(y * size) + x;
                byte level;
                if (cd)
                {
                    byte gray = (byte)(((red * 299) + (green * 587) + (blue * 114)) / 1000);
                    level = CdToneMapping.DisplayGray(CdToneMapping.Map(gray, normalized, index));
                }
                else
                {
                    int weighted = (2126 * red) + (7152 * green) + (722 * blue);
                    byte gray = (byte)(((weighted * alpha) + (2_550_000 * (255 - alpha)) + 1_275_000) / 2_550_000);
                    level = normalized.UseWhite(gray, index) ? (byte)255 : (byte)0;
                }

                after[to] = after[to + 1] = after[to + 2] = level;
                after[to + 3] = 255;
            }
        }

        return (before, after);
    }

    private static byte OverWhite(byte channel, byte alpha)
        => (byte)(((channel * alpha) + (255 * (255 - alpha)) + 127) / 255);
}
