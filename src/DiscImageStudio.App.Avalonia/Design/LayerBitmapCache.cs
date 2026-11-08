using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.App.Avalonia.Design;

/// <summary>A layer image as the canvas draws it; dimensions are the source's, for the aspect ratio.</summary>
public sealed record LayerBitmap(IImage? Image, int SourceWidth, int SourceHeight, bool IsMissing);

/// <summary>
/// Display bitmaps for the design canvas, at most <see cref="InteractionSide"/> px on the longest
/// side. Images come from the shared <see cref="CachingLayerImageSource"/>, so the canvas and the
/// composer decode each file version once. Loads run off the UI thread; the cache itself is only
/// touched on the UI thread, and <see cref="Changed"/> fires there when a load finishes.
/// </summary>
public sealed class LayerBitmapCache
{
    public const int InteractionSide = 1024;

    private readonly CachingLayerImageSource _images;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, (string Stamp, LayerBitmap Bitmap)> _bitmaps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private int _generation;

    public LayerBitmapCache(CachingLayerImageSource images, IUiDispatcher dispatcher)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event EventHandler? Changed;

    /// <summary>Returns the bitmap if ready, or null while it loads (a load is started if needed).</summary>
    public LayerBitmap? Get(string path)
    {
        string stamp = CachingLayerImageSource.DescribeFile(path);
        if (_bitmaps.TryGetValue(path, out (string Stamp, LayerBitmap Bitmap) cached) && cached.Stamp == stamp)
        {
            return cached.Bitmap;
        }

        if (stamp == "unavailable")
        {
            LayerBitmap missing = new(null, 0, 0, IsMissing: true);
            _bitmaps[path] = (stamp, missing);
            return missing;
        }

        if (_loading.Add(path))
        {
            int generation = _generation;
            _ = Task.Run(() => Load(path)).ContinueWith(
                task => _dispatcher.Post(() =>
                {
                    _loading.Remove(path);
                    if (generation != _generation)
                    {
                        return;
                    }

                    _bitmaps[path] = (stamp, task.IsCompletedSuccessfully ? task.Result : new LayerBitmap(null, 0, 0, true));
                    Changed?.Invoke(this, EventArgs.Empty);
                }),
                TaskScheduler.Default);
        }

        return null;
    }

    /// <summary>Drops every bitmap, for example when the project closes.</summary>
    public void Clear()
    {
        _generation++;
        _bitmaps.Clear();
        _loading.Clear();
    }

    private LayerBitmap Load(string path)
    {
        try
        {
            LayerImage image = _images.Load(path, CancellationToken.None);
            double factor = Math.Min(1.0, (double)InteractionSide / Math.Max(image.Width, image.Height));
            LayerImageLevel level = image.GetLevelFor(image.Width * factor, image.Height * factor);
            return new LayerBitmap(ToBitmap(level), image.Width, image.Height, IsMissing: false);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidDataException
                                              or ArgumentException
                                              or NotSupportedException
                                              or InvalidOperationException)
        {
            return new LayerBitmap(null, 0, 0, IsMissing: true);
        }
    }

    private static WriteableBitmap ToBitmap(LayerImageLevel level)
    {
        WriteableBitmap bitmap = new(
            new PixelSize(level.Width, level.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        int rowBytes = level.Width * 4;
        byte[] row = new byte[rowBytes];
        using ILockedFramebuffer buffer = bitmap.Lock();
        ReadOnlySpan<byte> pixels = level.PremultipliedPixels;
        for (int y = 0; y < level.Height; y++)
        {
            pixels.Slice(y * rowBytes, rowBytes).CopyTo(row);
            Marshal.Copy(row, 0, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }
}
