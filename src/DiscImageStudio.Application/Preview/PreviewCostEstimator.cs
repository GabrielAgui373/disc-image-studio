using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Preview;

/// <summary>
/// Expected cost of one preview, shown before the user picks a large resolution.
/// <see cref="WorkingBytes"/> is what the engine allocates for the output and its accumulators;
/// <see cref="SourceBytes"/> is the decoded source image (projection only), which is the peak
/// the 8192 px generation source adds while it loads.
/// </summary>
public sealed record PreviewCostEstimate(long Samples, long WorkingBytes, long SourceBytes, TimeSpan Duration)
{
    public long PeakBytes => WorkingBytes + SourceBytes;
}

/// <summary>
/// Order-of-magnitude cost model for each engine path. Sample counts and buffer sizes follow the
/// engines exactly; durations add the source decode to the engine's sampling, with throughputs
/// measured on a laptop CPU (Task 10.2: 4096 px source, default sizes), so hosts label them as
/// estimates.
/// </summary>
public static class PreviewCostEstimator
{
    private const long BytesPerPixel = 4;

    /// <summary>Track samples (or pixels, for Blu-ray) each path processes per second.</summary>
    internal static double SamplesPerSecond(OpticalDiscFamily family, PreviewMode mode) => (family, mode) switch
    {
        (OpticalDiscFamily.CompactDisc, _) => 5e6,
        (OpticalDiscFamily.Dvd, _) => 20e6,
        // Blu-ray measured 2.6 M px/s at 2048 px and 2.5 M px/s at 8192 px (Task 10.5).
        _ => 2.5e6,
    };

    /// <summary>Source pixels decoded (PNG to BGRA, then the engine's own conversion) per second.</summary>
    private const double SourcePixelsPerSecond = 40e6;

    public static PreviewCostEstimate Estimate(
        OpticalDiscFamily family,
        PreviewMode mode,
        int outputSize,
        long sectors,
        int samplesPerSector,
        int sourceSize = PreviewDefaults.MaximumSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sectors, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(samplesPerSector, 1);
        long pixels = (long)outputSize * outputSize;

        // Accumulators per pixel: CD sums in a long (data disc; audio uses an int, so this is an
        // upper bound) plus an int count, DVD weighted level and alpha sums; Blu-ray writes
        // pixels directly.
        long accumulatorBytesPerPixel = family switch
        {
            OpticalDiscFamily.CompactDisc => 12,
            OpticalDiscFamily.Dvd => 8,
            _ => 0,
        };
        long samples = family == OpticalDiscFamily.BluRay
            ? pixels
            : checked(sectors * samplesPerSector);
        long working = checked(pixels * (BytesPerPixel + accumulatorBytesPerPixel));
        // The engine decodes the composed source as BGRA before projecting.
        long source = mode == PreviewMode.Projection ? (long)sourceSize * sourceSize * BytesPerPixel : 0;
        double decodeSeconds = mode == PreviewMode.Projection ? (double)sourceSize * sourceSize / SourcePixelsPerSecond : 0;
        TimeSpan duration = TimeSpan.FromSeconds(decodeSeconds + (samples / SamplesPerSecond(family, mode)));
        return new PreviewCostEstimate(samples, working, source, duration);
    }
}
