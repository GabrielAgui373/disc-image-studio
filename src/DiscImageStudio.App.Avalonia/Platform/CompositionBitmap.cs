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

    /// <summary>Copies an 8-bit grey raster into a new opaque bitmap; safe off the UI thread.</summary>
    public static WriteableBitmap ToBitmap(int width, int height, byte[] gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int i = 0; i < gray.Length; i++)
        {
            int o = i * 4;
            bgra[o] = bgra[o + 1] = bgra[o + 2] = gray[i];
            bgra[o + 3] = 255;
        }

        return ToBitmap(new BgraRaster(width, height, bgra));
    }
}
