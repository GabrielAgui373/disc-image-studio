using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Rendering;

/// <summary>Supplies decoded layer images; 08.3 adds a caching implementation.</summary>
public interface ILayerImageSource
{
    /// <summary>Returns the decoded image or throws for a missing or unreadable file.</summary>
    LayerImage Load(string path, CancellationToken cancellationToken);
}

/// <summary>Decodes every request directly from disk, without caching.</summary>
public sealed class FileLayerImageSource : ILayerImageSource
{
    public static FileLayerImageSource Instance { get; } = new();

    public LayerImage Load(string path, CancellationToken cancellationToken) => LayerImage.Load(path, cancellationToken);
}

public enum CompositionIssueKind
{
    ImageMissing,
    ImageUnreadable,
    ImageTooLarge,
}

/// <summary>A layer that could not be painted; the rest of the composition is still produced.</summary>
public sealed record CompositionIssue(string LayerId, string ImagePath, CompositionIssueKind Kind, string Detail);

public sealed record CompositionResult(BgraRaster Image, IReadOnlyList<CompositionIssue> Issues);

/// <summary>
/// Deterministic, framework-free layer composer. It reproduces the WPF DesignComposer
/// geometry (see docs/IMAGE_COMPOSITION.md): a transparent square canvas centred on the disc,
/// layers sized by the content-ring fit rule, rotated about their centre and translated in
/// millimetres, painted bottom-first with source-over in premultiplied alpha.
/// </summary>
public static class ImageComposer
{
    /// <summary>
    /// Composes <paramref name="scene"/> into a <paramref name="size"/> × <paramref name="size"/>
    /// straight-alpha BGRA raster. Layers whose image cannot be loaded are skipped and reported.
    /// Cancellation throws <see cref="OperationCanceledException"/>; no partial result is returned.
    /// </summary>
    public static CompositionResult Compose(
        CompositionScene scene,
        int size,
        ILayerImageSource? images = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (size is < 1 or > CompositionLimits.MaximumCanvasSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                $"The canvas side must be between 1 and {CompositionLimits.MaximumCanvasSize} pixels.");
        }

        images ??= FileLayerImageSource.Instance;
        cancellationToken.ThrowIfCancellationRequested();
        byte[] canvas = new byte[checked(size * size * 4)];
        double pixelsPerMm = size / (2.0 * scene.CanvasOuterRadiusMm);
        List<CompositionIssue> issues = [];

        foreach (CompositionLayer layer in scene.Layers)
        {
            if (!layer.Visible || layer.Opacity <= 0)
            {
                continue;
            }

            LayerImage? image = TryLoad(layer, images, issues, cancellationToken);
            if (image is null)
            {
                continue;
            }

            Paint(canvas, size, layer, image, scene.ContentOuterRadiusMm, pixelsPerMm, cancellationToken);
        }

        Unpremultiply(canvas, cancellationToken);
        return new CompositionResult(new BgraRaster(size, size, canvas), issues);
    }

    /// <summary>Writes a composition as a BGRA PNG through <see cref="RasterCodec"/>.</summary>
    public static void SavePng(BgraRaster image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        RasterCodec.SavePng(path, image.Width, image.Height, image.Pixels, grayscale: false);
    }

    /// <summary>The layer's physical size at scale 1: its larger side spans the content-ring diameter.</summary>
    public static (double WidthMm, double HeightMm) BaseSizeMm(int imageWidth, int imageHeight, double contentOuterRadiusMm)
    {
        double diameter = 2.0 * contentOuterRadiusMm;
        return imageWidth >= imageHeight
            ? (diameter, diameter * imageHeight / imageWidth)
            : (diameter * imageWidth / imageHeight, diameter);
    }

    private static LayerImage? TryLoad(
        CompositionLayer layer,
        ILayerImageSource images,
        List<CompositionIssue> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            LayerImage image = images.Load(layer.ImagePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            issues.Add(new CompositionIssue(layer.Id, layer.ImagePath, CompositionIssueKind.ImageMissing, exception.Message));
        }
        catch (ArgumentException exception)
        {
            // RasterCodec reports file-size and pixel-count limits as ArgumentException.
            issues.Add(new CompositionIssue(layer.Id, layer.ImagePath, CompositionIssueKind.ImageTooLarge, exception.Message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Codec failures on corrupt data surface as several exception types (including the
            // decoder's own); any of them means this one layer is unreadable, not the scene.
            issues.Add(new CompositionIssue(layer.Id, layer.ImagePath, CompositionIssueKind.ImageUnreadable, exception.Message));
        }

        return null;
    }

    private static void Paint(
        byte[] canvas,
        int size,
        CompositionLayer layer,
        LayerImage image,
        double contentOuterRadiusMm,
        double pixelsPerMm,
        CancellationToken cancellationToken)
    {
        (double baseWidthMm, double baseHeightMm) = BaseSizeMm(image.Width, image.Height, contentOuterRadiusMm);
        double width = baseWidthMm * layer.Scale * pixelsPerMm;
        double height = baseHeightMm * layer.Scale * pixelsPerMm;
        if (!(width > 0) || !(height > 0))
        {
            return;
        }

        LayerImageLevel level = image.GetLevelFor(width, height, cancellationToken);
        double centreX = (size / 2.0) + (layer.TranslateXMm * pixelsPerMm);
        double centreY = (size / 2.0) + (layer.TranslateYMm * pixelsPerMm);
        double radians = layer.RotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        // Destination bounds of the rotated rectangle, grown by the 1 px edge ramp.
        double halfX = (Math.Abs(width / 2 * cos) + Math.Abs(height / 2 * sin)) + 1;
        double halfY = (Math.Abs(width / 2 * sin) + Math.Abs(height / 2 * cos)) + 1;
        int x0 = Math.Max(0, (int)Math.Floor(centreX - halfX));
        int x1 = Math.Min(size, (int)Math.Ceiling(centreX + halfX));
        int y0 = Math.Max(0, (int)Math.Floor(centreY - halfY));
        int y1 = Math.Min(size, (int)Math.Ceiling(centreY + halfY));
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        byte[] source = level.Pixels;
        int sourceWidth = level.Width;
        int sourceHeight = level.Height;
        double toSourceX = sourceWidth / width;
        double toSourceY = sourceHeight / height;
        double opacity = layer.Opacity;

        Parallel.For(y0, y1, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            double dy = y + 0.5 - centreY;
            for (int x = x0; x < x1; x++)
            {
                // Inverse transform of the pixel centre into the layer's own pixel space.
                double dx = x + 0.5 - centreX;
                double lx = (dx * cos) + (dy * sin) + (width / 2);
                double ly = (-dx * sin) + (dy * cos) + (height / 2);

                // Linear 1 px coverage ramp across each edge (anti-aliased rotated borders).
                double coverage = Math.Clamp(Math.Min(lx, width - lx) + 0.5, 0, 1)
                                  * Math.Clamp(Math.Min(ly, height - ly) + 0.5, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }

                double u = Math.Clamp((lx * toSourceX) - 0.5, 0, sourceWidth - 1);
                double v = Math.Clamp((ly * toSourceY) - 0.5, 0, sourceHeight - 1);
                int iu = (int)u;
                int iv = (int)v;
                int iu1 = Math.Min(iu + 1, sourceWidth - 1);
                int iv1 = Math.Min(iv + 1, sourceHeight - 1);
                double fu = u - iu;
                double fv = v - iv;
                double w00 = (1 - fu) * (1 - fv);
                double w10 = fu * (1 - fv);
                double w01 = (1 - fu) * fv;
                double w11 = fu * fv;
                int p00 = ((iv * sourceWidth) + iu) * 4;
                int p10 = ((iv * sourceWidth) + iu1) * 4;
                int p01 = ((iv1 * sourceWidth) + iu) * 4;
                int p11 = ((iv1 * sourceWidth) + iu1) * 4;
                double factor = coverage * opacity;

                double sa = Sample(source, p00, p10, p01, p11, 3, w00, w10, w01, w11) * factor;
                if (sa <= 0)
                {
                    continue;
                }

                double inverse = 1 - (sa / 255.0);
                int d = ((y * size) + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double sc = Sample(source, p00, p10, p01, p11, channel, w00, w10, w01, w11) * factor;
                    canvas[d + channel] = Quantize(sc + (canvas[d + channel] * inverse));
                }

                canvas[d + 3] = Quantize(sa + (canvas[d + 3] * inverse));
            }
        });
    }

    private static double Sample(
        byte[] source,
        int p00,
        int p10,
        int p01,
        int p11,
        int channel,
        double w00,
        double w10,
        double w01,
        double w11)
        => (source[p00 + channel] * w00) + (source[p10 + channel] * w10)
           + (source[p01 + channel] * w01) + (source[p11 + channel] * w11);

    private static byte Quantize(double value)
        => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Converts the premultiplied canvas to straight alpha in place (no second buffer).</summary>
    private static void Unpremultiply(byte[] canvas, CancellationToken cancellationToken)
    {
        int pixels = canvas.Length / 4;
        const int Block = 1 << 16;
        Parallel.For(0, (pixels + Block - 1) / Block, new ParallelOptions { CancellationToken = cancellationToken }, block =>
        {
            int end = Math.Min(pixels, (block + 1) * Block);
            for (int pixel = block * Block; pixel < end; pixel++)
            {
                int i = pixel * 4;
                int alpha = canvas[i + 3];
                if (alpha is 0 or 255)
                {
                    if (alpha == 0)
                    {
                        canvas[i] = canvas[i + 1] = canvas[i + 2] = 0;
                    }

                    continue;
                }

                // round(c × 255 / a), half away from zero, in integers.
                canvas[i] = (byte)Math.Min(255, ((canvas[i] * 255) + (alpha / 2)) / alpha);
                canvas[i + 1] = (byte)Math.Min(255, ((canvas[i + 1] * 255) + (alpha / 2)) / alpha);
                canvas[i + 2] = (byte)Math.Min(255, ((canvas[i + 2] * 255) + (alpha / 2)) / alpha);
            }
        });
    }
}
