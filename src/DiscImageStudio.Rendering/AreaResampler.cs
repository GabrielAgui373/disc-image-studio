namespace DiscImageStudio.Rendering;

/// <summary>
/// Exact area-average (box) reduction of premultiplied BGRA, done as two separable passes.
/// Used once when a layer is loaded above the side limit; every destination pixel averages
/// the source area it covers, with fractional weights at its edges.
/// </summary>
internal static class AreaResampler
{
    public static byte[] Resample(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (width > sourceWidth || height > sourceHeight)
        {
            throw new ArgumentException("Area resampling only reduces.");
        }

        // Horizontal pass into a float buffer (sourceHeight × width), then vertical into bytes.
        float[] horizontal = new float[checked((long)sourceHeight * width * 4)];
        (int Start, int End, float[] Weights)[] columns = Spans(sourceWidth, width);
        ParallelOptions options = new() { CancellationToken = cancellationToken };
        Parallel.For(0, sourceHeight, options, y =>
        {
            int row = y * sourceWidth * 4;
            for (int x = 0; x < width; x++)
            {
                (int start, int end, float[] weights) = columns[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int s = start; s < end; s++)
                {
                    float w = weights[s - start];
                    int i = row + (s * 4);
                    b += source[i] * w;
                    g += source[i + 1] * w;
                    r += source[i + 2] * w;
                    a += source[i + 3] * w;
                }

                long o = (((long)y * width) + x) * 4;
                horizontal[o] = b;
                horizontal[o + 1] = g;
                horizontal[o + 2] = r;
                horizontal[o + 3] = a;
            }
        });

        (int Start, int End, float[] Weights)[] rows = Spans(sourceHeight, height);
        byte[] result = new byte[checked(width * height * 4)];
        Parallel.For(0, height, options, y =>
        {
            (int start, int end, float[] weights) = rows[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int s = start; s < end; s++)
                {
                    float w = weights[s - start];
                    long i = (((long)s * width) + x) * 4;
                    b += horizontal[i] * w;
                    g += horizontal[i + 1] * w;
                    r += horizontal[i + 2] * w;
                    a += horizontal[i + 3] * w;
                }

                int o = ((y * width) + x) * 4;
                result[o] = ToByte(b);
                result[o + 1] = ToByte(g);
                result[o + 2] = ToByte(r);
                result[o + 3] = ToByte(a);
            }
        });
        return result;
    }

    /// <summary>Source index range and normalized coverage weights of each destination index.</summary>
    private static (int Start, int End, float[] Weights)[] Spans(int sourceLength, int length)
    {
        double ratio = (double)sourceLength / length;
        (int, int, float[])[] spans = new (int, int, float[])[length];
        for (int d = 0; d < length; d++)
        {
            double from = d * ratio;
            double to = (d + 1) * ratio;
            int start = (int)Math.Floor(from);
            int end = Math.Min(sourceLength, (int)Math.Ceiling(to));
            float[] weights = new float[end - start];
            for (int s = start; s < end; s++)
            {
                double overlap = Math.Min(to, s + 1) - Math.Max(from, s);
                weights[s - start] = (float)(overlap / ratio);
            }

            spans[d] = (start, end, weights);
        }

        return spans;
    }

    private static byte ToByte(float value)
        => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}
