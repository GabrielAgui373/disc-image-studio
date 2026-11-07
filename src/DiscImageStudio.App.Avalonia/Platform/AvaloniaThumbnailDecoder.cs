using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DiscImageStudio.App.Avalonia.Platform;

/// <summary>
/// Decodes project thumbnails into display-sized images. Implementations must be safe to call
/// from a background thread and return <see langword="null"/> instead of throwing for bad data.
/// </summary>
public interface IThumbnailDecoder
{
    IImage? Decode(byte[] png, int decodeWidth);
}

public sealed class AvaloniaThumbnailDecoder : IThumbnailDecoder
{
    public IImage? Decode(byte[] png, int decodeWidth)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            using MemoryStream stream = new(png, writable: false);
            return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            // The payload already passed size and signature checks; any remaining decoder
            // failure (truncated or corrupt PNG) falls back to the family artwork.
            return null;
        }
    }
}
