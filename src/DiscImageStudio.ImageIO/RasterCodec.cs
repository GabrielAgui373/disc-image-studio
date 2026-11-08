namespace DiscImageStudio.ImageIO;

public sealed record BgraRaster(int Width, int Height, byte[] Pixels);

/// <summary>Reads the first frame as straight BGRA32 and writes a Gray8 or BGRA32 PNG.</summary>
public static partial class RasterCodec
{
    public static BgraRaster LoadBgra32(string path, long maxFileBytes = long.MaxValue, long maxPixels = int.MaxValue / 4)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        CheckFileSize(stream, maxFileBytes);
        return Decode(stream, maxPixels);
    }

    public static void ValidateSource(string path, long maxFileBytes, long maxPixels)
    {
        using FileStream stream = File.OpenRead(Path.GetFullPath(path));
        CheckFileSize(stream, maxFileBytes);
        _ = ReadHeader(stream, maxPixels);
    }

    public static void SavePng(string path, int width, int height, byte[] pixels, bool grayscale)
    {
        ValidatePixels(width, height, pixels, grayscale);
        using FileStream output = File.Create(path);
        WritePng(output, width, height, pixels, grayscale);
    }

    /// <summary>
    /// Writes a straight-alpha BGRA32 raster as PNG and may reorder its channels in place to do
    /// so, avoiding a full-size copy. The caller must not read <paramref name="raster"/> afterwards.
    /// Meant for large one-shot buffers such as the 8192 px generation source.
    /// </summary>
    public static void SavePngConsumingBuffer(string path, BgraRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ValidatePixels(raster.Width, raster.Height, raster.Pixels, grayscale: false);
        using FileStream output = File.Create(path);
        WritePng(output, raster.Width, raster.Height, raster.Pixels, grayscale: false, mayModifyPixels: true);
    }

    /// <summary>Encodes a Gray8 or straight-alpha BGRA32 buffer as PNG bytes, without touching disk.</summary>
    public static byte[] EncodePng(int width, int height, byte[] pixels, bool grayscale)
    {
        ValidatePixels(width, height, pixels, grayscale);
        using MemoryStream output = new();
        WritePng(output, width, height, pixels, grayscale);
        return output.ToArray();
    }

    private static void ValidatePixels(int width, int height, byte[] pixels, bool grayscale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(width * height * (grayscale ? 1 : 4)))
            throw new ArgumentException("Pixel buffer length does not match the dimensions and format.", nameof(pixels));
    }

    private static void CheckFileSize(FileStream stream, long maxBytes)
    {
        if (stream.Length > maxBytes)
            throw new ArgumentException($"The source image must be no larger than {maxBytes / 1_048_576d:G} MiB.", "path");
    }

    private static void CheckDimensions(int width, int height, long maxPixels)
    {
        if (width <= 0 || height <= 0 || (long)width * height > Math.Min(maxPixels, int.MaxValue / 4))
            throw new ArgumentException(FormattableString.Invariant(
                $"The source image must contain between 1 and {Math.Min(maxPixels, int.MaxValue / 4):N0} pixels."), "path");
    }
}
