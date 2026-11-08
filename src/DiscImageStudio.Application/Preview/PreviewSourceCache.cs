using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.Application.Preview;

/// <summary>
/// The composed generation source a projection reads: the project at 8192 px, exactly what
/// generation would consume, written once per design content and shared by every preview of
/// that content. Changing resolution, mode or geometry therefore never recomposes; a design
/// change composes once. The file stays until no lease holds it and newer content replaced it.
/// </summary>
public sealed class PreviewSourceCache : IDisposable
{
    private readonly ProjectCompositionService _compositions;
    private readonly string _directory;
    private readonly Lock _lock = new();
    private Entry? _current;
    private bool _disposed;

    public PreviewSourceCache(ProjectCompositionService compositions, string directory)
    {
        _compositions = compositions ?? throw new ArgumentNullException(nameof(compositions));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>
    /// Returns a lease on the source for <paramref name="workspace"/>, composing it if the cached
    /// one describes other content. Dispose the lease once the engine has loaded the image.
    /// </summary>
    public async Task<PreviewSourceLease> AcquireAsync(
        ProjectWorkspace workspace,
        string? projectFilePath,
        CancellationToken cancellationToken)
        => await AcquireAsync(workspace, projectFilePath, ProjectCompositionService.GenerationSize, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// As <see cref="AcquireAsync(ProjectWorkspace, string?, CancellationToken)"/> at
    /// <paramref name="size"/> px. Smaller sources cost a quarter of the memory per halving but
    /// no longer match generation pixel for pixel.
    /// </summary>
    public async Task<PreviewSourceLease> AcquireAsync(
        ProjectWorkspace workspace,
        string? projectFilePath,
        int size,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string key = await Task.Run(() => _compositions.ContentKey(workspace, projectFilePath), cancellationToken)
            .ConfigureAwait(false);
        key = $"{size}|{key}";
        for (int attempt = 0; ; attempt++)
        {
            Entry entry = Lease(key, workspace, projectFilePath, size);
            try
            {
                GenerationSourceImage source = await entry.Source.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new PreviewSourceLease(this, entry, source);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 0)
            {
                // The entry was canceled because newer content replaced it while it composed and
                // nobody else wanted it; compose again for this request.
                Release(entry);
            }
            catch
            {
                Release(entry);
                throw;
            }
        }
    }

    public void Dispose()
    {
        Entry? retired;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            retired = _current;
            _current = null;
            retired?.Retire();
        }

        retired?.CollectIfUnused();
    }

    private Entry Lease(string key, ProjectWorkspace workspace, string? projectFilePath, int size)
    {
        Entry? retired = null;
        Entry entry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { } current && current.Key == key && !current.Source.IsFaulted && !current.Source.IsCanceled)
            {
                entry = current;
            }
            else
            {
                retired = _current;
                retired?.Retire();
                entry = new Entry(key);
                entry.Start(() => _compositions.PrepareSourceImage(workspace, projectFilePath, _directory, size, entry.Token));
                _current = entry;
            }

            entry.Leases++;
        }

        retired?.CollectIfUnused();
        return entry;
    }

    internal void Release(Entry entry)
    {
        lock (_lock)
        {
            entry.Leases--;
        }

        entry.CollectIfUnused();
    }

    internal sealed class Entry(string key)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _collected;

        public string Key { get; } = key;

        public Task<GenerationSourceImage> Source { get; private set; } = null!;

        public CancellationToken Token => _cancellation.Token;

        public int Leases { get; set; }

        public bool Retired { get; private set; }

        public void Start(Func<GenerationSourceImage> compose)
            => Source = Task.Run(compose, _cancellation.Token);

        public void Retire() => Retired = true;

        /// <summary>Cancels a retired entry nobody holds and deletes its file once composed.</summary>
        public void CollectIfUnused()
        {
            if (!Retired || Leases > 0 || Interlocked.Exchange(ref _collected, 1) == 1)
            {
                return;
            }

            _cancellation.Cancel();
            _ = Source.ContinueWith(
                static task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        task.Result.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

/// <summary>A held generation source. Disposing releases it; the file may be deleted afterwards.</summary>
public sealed class PreviewSourceLease : IDisposable
{
    private readonly PreviewSourceCache _owner;
    private readonly PreviewSourceCache.Entry _entry;
    private int _disposed;

    internal PreviewSourceLease(PreviewSourceCache owner, PreviewSourceCache.Entry entry, GenerationSourceImage source)
    {
        _owner = owner;
        _entry = entry;
        Path = source.Path;
        Size = source.Size;
        PaintedLayerCount = source.PaintedLayerCount;
        Issues = source.Issues;
    }

    public string Path { get; }

    /// <summary>Side of the composed source, in pixels.</summary>
    public int Size { get; }

    public int PaintedLayerCount { get; }

    public IReadOnlyList<CompositionIssue> Issues { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release(_entry);
        }
    }
}
