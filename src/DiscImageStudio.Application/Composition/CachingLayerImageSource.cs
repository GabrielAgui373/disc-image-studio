using DiscImageStudio.Application.Projects;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.Application.Composition;

/// <summary>
/// Keeps decoded layer images between compositions, keyed by normalized path and validated by
/// file length and last-write time so a replaced file is decoded again. Least-recently-used
/// images are dropped once the byte budget is exceeded. A load that fails or is canceled never
/// leaves an entry behind.
/// </summary>
public sealed class CachingLayerImageSource : ILayerImageSource
{
    /// <summary>Default budget: four 4096 px layers with their reduction pyramids.</summary>
    public const long DefaultBudgetBytes = 384L * 1024 * 1024;

    private readonly ILayerImageSource _inner;
    private readonly long _budgetBytes;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(ProjectPaths.Comparer);
    private long _clock;

    public CachingLayerImageSource(ILayerImageSource? inner = null, long budgetBytes = DefaultBudgetBytes)
    {
        _inner = inner ?? FileLayerImageSource.Instance;
        _budgetBytes = budgetBytes > 0 ? budgetBytes : throw new ArgumentOutOfRangeException(nameof(budgetBytes));
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public LayerImage Load(string path, CancellationToken cancellationToken)
    {
        string key = ProjectPaths.TryNormalize(path) ?? path;
        FileStamp stamp = FileStamp.Read(key);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Entry? cached) && cached.Stamp == stamp)
            {
                cached.LastUse = ++_clock;
                return cached.Image;
            }
        }

        // Decode outside the lock so independent layers load in parallel; a canceled or failed
        // decode throws before anything is stored.
        LayerImage image = _inner.Load(key, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _entries[key] = new Entry(stamp, image) { LastUse = ++_clock };
            Evict(keep: key);
        }

        return image;
    }

    /// <summary>Drops every cached image, for example when the project closes.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <summary>Stamp of a file as the cache sees it; also used to key compositions.</summary>
    public static string DescribeFile(string path)
    {
        try
        {
            FileStamp stamp = FileStamp.Read(path);
            return $"{stamp.Length}:{stamp.LastWriteUtcTicks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unavailable";
        }
    }

    private void Evict(string keep)
    {
        long total = _entries.Values.Sum(entry => entry.Image.ByteCount);
        while (total > _budgetBytes && _entries.Count > 1)
        {
            KeyValuePair<string, Entry> oldest = _entries
                .Where(pair => !ProjectPaths.Comparer.Equals(pair.Key, keep))
                .MinBy(pair => pair.Value.LastUse);
            if (oldest.Key is null)
            {
                return;
            }

            total -= oldest.Value.Image.ByteCount;
            _entries.Remove(oldest.Key);
        }
    }

    private sealed class Entry(FileStamp stamp, LayerImage image)
    {
        public FileStamp Stamp { get; } = stamp;

        public LayerImage Image { get; } = image;

        public long LastUse { get; set; }
    }

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks)
    {
        public static FileStamp Read(string path)
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The layer image does not exist.", path);
            }

            return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }
}
