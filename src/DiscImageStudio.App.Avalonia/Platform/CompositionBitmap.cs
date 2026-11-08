using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.App.Avalonia.Platform;

/// <summary>
/// The only bridge from the portable composer's <see cref="BgraRaster"/> to an Avalonia bitmap.
/// The composer works in its own buffers; Avalonia types never enter the engine.
/// </summary>
public static class CompositionBitmap
{
    /// <summary>Copies a straight-alpha BGRA raster into a new bitmap; safe off the UI thread.</summary>
    public static WriteableBitmap ToBitmap(BgraRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        WriteableBitmap bitmap = new(
            new PixelSize(raster.Width, raster.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        int rowBytes = raster.Width * 4;
        using ILockedFramebuffer buffer = bitmap.Lock();
        for (int y = 0; y < raster.Height; y++)
        {
            Marshal.Copy(raster.Pixels, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }
}
