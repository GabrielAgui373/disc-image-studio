namespace DiscImageStudio.Rendering;

/// <summary>Documented bounds of the composer; see docs/IMAGE_COMPOSITION.md.</summary>
public static class CompositionLimits
{
    /// <summary>Largest canvas side: the generation size the disc engines consume.</summary>
    public const int MaximumCanvasSize = 8192;

    /// <summary>Longest side a layer keeps after loading; larger images are area-averaged down once.</summary>
    public const int MaximumLayerSide = 4096;

    /// <summary>Source pixels accepted before the load-time reduction (bounds the transient decode buffer).</summary>
    public const long MaximumSourcePixels = 64L * 1024 * 1024;

    /// <summary>Source files larger than this are rejected before decoding.</summary>
    public const long MaximumSourceFileBytes = 256L * 1024 * 1024;
}
