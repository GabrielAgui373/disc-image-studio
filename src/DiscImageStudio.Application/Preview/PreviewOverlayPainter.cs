namespace DiscImageStudio.Application.Preview;

/// <summary>
/// Burns ring overlays into a copy of a preview's pixels, for export and for hosts that show the
/// overlay as part of the picture. Filesystem space is filled with the WPF legend colour
/// (#FACC15); the useful ring is outlined. Only presentation: the generated artifact and burn
/// streams never pass through here.
/// </summary>
public static class PreviewOverlayPainter
{
    // BGRA of #FACC15, the WPF preview legend colour for filesystem space.
    private static ReadOnlySpan<byte> FileSystemColor => [21, 204, 250, 255];

    // BGRA of #38BDF8, a cyan that stays visible on both black and white track pixels.
    private static ReadOnlySpan<byte> RingColor => [248, 189, 56, 255];

    public static byte[] Paint(
        PreviewResult result,
        bool fileSystem = true,
        bool usefulRing = false,
        CancellationToken cancellationToken = default)
        => Paint(result, result?.Pixels!, fileSystem, usefulRing, cancellationToken);

    /// <summary>
    /// As <see cref="Paint(PreviewResult, bool, bool, CancellationToken)"/> over
    /// <paramref name="basePixels"/>, a recolouring of the result in the same frame (for example
    /// the stylized media appearance).
    /// </summary>
    public static byte[] Paint(
        PreviewResult result,
        byte[] basePixels,
        bool fileSystem,
        bool usefulRing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(basePixels);
        if (basePixels.Length != result.Pixels.Length)
        {
            throw new ArgumentException("The base pixels must match the result's size.", nameof(basePixels));
        }

        byte[] pixels = (byte[])basePixels.Clone();
        int size = result.Size;
        PreviewRingOverlay[] fills = fileSystem
            ? result.Overlays.Where(overlay => overlay.Kind == PreviewOverlayKind.FileSystem).ToArray()
            : [];
        PreviewRingOverlay[] outlines = usefulRing
            ? result.Overlays.Where(overlay => overlay.Kind == PreviewOverlayKind.UsefulRing).ToArray()
            : [];
        if (fills.Length == 0 && outlines.Length == 0)
        {
            return pixels;
        }

        // A thin outline: a pixel is on the circle when its centre lies within three quarters
        // of a pixel of it, which leaves no gaps along the diagonal.
        double halfPixelMm = result.Frame.MmPerPixel * 0.75;
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                double radius = result.Frame.RadiusMmAt(x, y);
                ReadOnlySpan<byte> color = default;
                foreach (PreviewRingOverlay ring in fills)
                {
                    if (radius >= ring.InnerRadiusMm && radius < ring.OuterRadiusMm)
                    {
                        color = FileSystemColor;
                        break;
                    }
                }

                foreach (PreviewRingOverlay ring in outlines)
                {
                    if (Math.Abs(radius - ring.InnerRadiusMm) <= halfPixelMm
                        || Math.Abs(radius - ring.OuterRadiusMm) <= halfPixelMm)
                    {
                        color = RingColor;
                        break;
                    }
                }

                if (!color.IsEmpty)
                {
                    color.CopyTo(pixels.AsSpan(((y * size) + x) * 4, 4));
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// The filesystem annuli of a track whose drawing occupies
    /// [<paramref name="drawingInnerMm"/>, <paramref name="drawingOuterMm"/>): whatever the track
    /// covers outside that range. Empty parts are omitted.
    /// </summary>
    public static IReadOnlyList<PreviewRingOverlay> FileSystemRings(
        double trackInnerMm,
        double trackOuterMm,
        double drawingInnerMm,
        double drawingOuterMm)
    {
        // Rounding in the radius-from-sector functions can leave a sliver of a few nanometres
        // where the drawing reaches the track edge; that is not filesystem space.
        const double MinimumWidthMm = 1e-3;
        List<PreviewRingOverlay> rings = [];
        if (drawingInnerMm - trackInnerMm > MinimumWidthMm)
        {
            rings.Add(new PreviewRingOverlay(PreviewOverlayKind.FileSystem, trackInnerMm, drawingInnerMm));
        }

        if (trackOuterMm - drawingOuterMm > MinimumWidthMm)
        {
            rings.Add(new PreviewRingOverlay(PreviewOverlayKind.FileSystem, drawingOuterMm, trackOuterMm));
        }

        return rings;
    }
}
