using System.Globalization;
using System.Text;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.Application.Composition;

/// <summary>A finished composition of the project's design layers.</summary>
public sealed record ProjectComposition(
    BgraRaster Image,
    IReadOnlyList<CompositionIssue> Issues,
    int Size,
    long Revision,
    int PaintedLayerCount,
    double CanvasOuterRadiusMm,
    double ContentInnerRadiusMm);

/// <summary>Renders the thumbnail stored in a project file when it is saved.</summary>
public interface IProjectThumbnailRenderer
{
    /// <summary>PNG Base64 of the design, or <see langword="null"/> when nothing could be painted.</summary>
    string? RenderThumbnail(ProjectWorkspace workspace, string? projectFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a <see cref="ProjectWorkspace"/> into a <see cref="CompositionScene"/> and composes it
/// with cached decoded layers. Only outputs up to <see cref="MaximumCachedSize"/> are cached
/// (one per size), so the service never keeps an 8192 px buffer alive. Requests on a named
/// channel supersede older ones: the older request is canceled and never reported as current.
/// </summary>
public sealed class ProjectCompositionService : IProjectThumbnailRenderer
{
    public const int ThumbnailSize = ProjectDocument.ThumbnailDecodeSize;
    public const int PreviewSize = 2048;
    public const int GenerationSize = CompositionLimits.MaximumCanvasSize;
    public const int MaximumCachedSize = PreviewSize;

    private readonly double _cdCanvasOuterRadiusMm;
    private readonly CachingLayerImageSource _images;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (string SceneKey, ProjectComposition Result)> _composites = [];
    private readonly Dictionary<string, (long Revision, CancellationTokenSource Cancellation)> _channels = new(StringComparer.Ordinal);
    private long _revision;

    /// <param name="cdCanvasOuterRadiusMm">
    /// The CD scheme's fixed image outer radius, supplied by the host from the CD module so this
    /// layer stays independent of the disc engines.
    /// </param>
    public ProjectCompositionService(double cdCanvasOuterRadiusMm, CachingLayerImageSource? images = null)
    {
        _cdCanvasOuterRadiusMm = double.IsFinite(cdCanvasOuterRadiusMm) && cdCanvasOuterRadiusMm > 0
            ? cdCanvasOuterRadiusMm
            : throw new ArgumentOutOfRangeException(nameof(cdCanvasOuterRadiusMm));
        _images = images ?? new CachingLayerImageSource();
    }

    /// <summary>Decoded-layer cache shared with hosts that draw layers themselves (the design canvas).</summary>
    public CachingLayerImageSource LayerImages => _images;

    /// <summary>
    /// Builds the scene: CD composes against the scheme's fixed image radius, DVD and BD against
    /// the generated outer radius, and layer sizes follow the content outer radius (WPF rules).
    /// Relative asset paths resolve against the project folder.
    /// </summary>
    public CompositionScene CreateScene(ProjectWorkspace workspace, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new CompositionScene(
            CanvasOuterRadius(workspace.Media),
            workspace.Media.OuterRadiusMm,
            workspace.Design.Layers.Select(layer => new CompositionLayer(
                layer.Id,
                ProjectPaths.ResolveAsset(projectFilePath, layer.ImagePath) ?? layer.ImagePath,
                layer.TranslateXMm,
                layer.TranslateYMm,
                layer.RotationDeg,
                layer.Scale,
                layer.Visible,
                layer.Opacity)));
    }

    /// <summary>Composes synchronously on the calling thread; callers keep it off the UI thread.</summary>
    public ProjectComposition Compose(
        ProjectWorkspace workspace,
        string? projectFilePath,
        int size,
        CancellationToken cancellationToken = default)
        => Compose(workspace, projectFilePath, size, Interlocked.Increment(ref _revision), cancellationToken);

    /// <summary>
    /// Composes on a background thread as the newest request of <paramref name="channel"/>. A
    /// newer request on the same channel cancels this one; a superseded request returns
    /// <see langword="null"/> so a stale preview can never replace a newer one.
    /// </summary>
    public async Task<ProjectComposition?> ComposeLatestAsync(
        string channel,
        ProjectWorkspace workspace,
        string? projectFilePath,
        int size,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        long revision = Interlocked.Increment(ref _revision);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            if (_channels.TryGetValue(channel, out (long Revision, CancellationTokenSource Cancellation) previous))
            {
                previous.Cancellation.Cancel();
            }

            _channels[channel] = (revision, linked);
        }

        try
        {
            ProjectComposition result = await Task.Run(
                () => Compose(workspace, projectFilePath, size, revision, linked.Token),
                linked.Token);
            return IsCurrent(channel, revision) ? result : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer request on the channel.
            return null;
        }
        finally
        {
            lock (_lock)
            {
                if (_channels.TryGetValue(channel, out (long Revision, CancellationTokenSource Cancellation) current)
                    && current.Revision == revision)
                {
                    _channels.Remove(channel);
                }
            }

            linked.Dispose();
        }
    }

    public string? RenderThumbnail(
        ProjectWorkspace workspace,
        string? projectFilePath,
        CancellationToken cancellationToken = default)
    {
        ProjectComposition composition = Compose(workspace, projectFilePath, ThumbnailSize, cancellationToken);
        if (composition.PaintedLayerCount == 0)
        {
            return null;
        }

        byte[] png = RasterCodec.EncodePng(composition.Image.Width, composition.Image.Height, composition.Image.Pixels, grayscale: false);
        return Convert.ToBase64String(png);
    }

    /// <summary>
    /// Composes the project at <see cref="GenerationSize"/> and writes it transactionally into
    /// <paramref name="directory"/>: the PNG is written under a hidden <c>.partial</c> name and only
    /// renamed to its final name once complete, so a canceled or failed run never leaves a
    /// half-written source image behind. The 8192 px buffer is released before this returns.
    /// Blocking; callers run it off the UI thread.
    /// </summary>
    public GenerationSourceImage PrepareGenerationSource(
        ProjectWorkspace workspace,
        string? projectFilePath,
        string directory,
        CancellationToken cancellationToken = default)
        => PrepareSourceImage(workspace, projectFilePath, directory, GenerationSize, cancellationToken);

    /// <summary>
    /// <see cref="PrepareGenerationSource"/> at another side length, for previews that trade
    /// source resolution for memory. Generation itself always uses <see cref="GenerationSize"/>.
    /// </summary>
    public GenerationSourceImage PrepareSourceImage(
        ProjectWorkspace workspace,
        string? projectFilePath,
        string directory,
        int size,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, GenerationSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        string name = $"generation-source-{Guid.NewGuid():N}.png";
        string finalPath = Path.Combine(directory, name);
        string partialPath = Path.Combine(directory, $".{name}.partial");
        try
        {
            int painted;
            IReadOnlyList<CompositionIssue> issues;
            {
                // Scoped so the composition buffer is unreachable once the PNG is written.
                ProjectComposition composition = Compose(workspace, projectFilePath, size, cancellationToken);
                painted = composition.PaintedLayerCount;
                issues = composition.Issues;
                cancellationToken.ThrowIfCancellationRequested();
                RasterCodec.SavePngConsumingBuffer(partialPath, composition.Image);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath, overwrite: false);
            return new GenerationSourceImage(finalPath, size, painted, issues);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    public Task<GenerationSourceImage> PrepareGenerationSourceAsync(
        ProjectWorkspace workspace,
        string? projectFilePath,
        string directory,
        CancellationToken cancellationToken = default)
        => Task.Run(() => PrepareGenerationSource(workspace, projectFilePath, directory, cancellationToken), cancellationToken);

    /// <summary>Releases cached layers and compositions, for example when the project closes.</summary>
    public void Clear()
    {
        _images.Clear();
        lock (_lock)
        {
            _composites.Clear();
        }
    }

    private ProjectComposition Compose(
        ProjectWorkspace workspace,
        string? projectFilePath,
        int size,
        long revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        CompositionScene scene = CreateScene(workspace, projectFilePath);
        string sceneKey = SceneKey(scene);
        lock (_lock)
        {
            if (_composites.TryGetValue(size, out (string SceneKey, ProjectComposition Result) cached)
                && cached.SceneKey == sceneKey)
            {
                return cached.Result with { Revision = revision };
            }
        }

        CompositionResult composed = ImageComposer.Compose(scene, size, _images, cancellationToken);
        int visible = scene.Layers.Count(layer => layer.Visible && layer.Opacity > 0);
        ProjectComposition result = new(
            composed.Image,
            composed.Issues,
            size,
            revision,
            Math.Max(0, visible - composed.Issues.Count),
            scene.CanvasOuterRadiusMm,
            workspace.Media.InnerRadiusMm);
        if (size <= MaximumCachedSize)
        {
            // Completed results only: a canceled composition threw above and caches nothing.
            lock (_lock)
            {
                _composites[size] = (sceneKey, result);
            }
        }

        return result;
    }

    private bool IsCurrent(string channel, long revision)
    {
        lock (_lock)
        {
            return _channels.TryGetValue(channel, out (long Revision, CancellationTokenSource Cancellation) current)
                   && current.Revision == revision;
        }
    }

    /// <summary>
    /// Identity of what <paramref name="workspace"/> composes to: canvas, layer transforms and each
    /// image file's length and timestamp. Equal keys compose to identical images, so callers can
    /// cache composed output (for example the preview's generation source) by it.
    /// </summary>
    public string ContentKey(ProjectWorkspace workspace, string? projectFilePath)
        => SceneKey(CreateScene(workspace, projectFilePath));

    private double CanvasOuterRadius(MediaState media)
        => media is CdState ? _cdCanvasOuterRadiusMm : media.OuterRadiusMm;

    /// <summary>Content identity of a scene, including each image file's length and timestamp.</summary>
    private static string SceneKey(CompositionScene scene)
    {
        StringBuilder key = new();
        key.Append(CultureInfo.InvariantCulture, $"{scene.CanvasOuterRadiusMm:R}|{scene.ContentOuterRadiusMm:R}");
        foreach (CompositionLayer layer in scene.Layers)
        {
            key.Append(CultureInfo.InvariantCulture, $"|{layer.Id}:{layer.ImagePath}:{CachingLayerImageSource.DescribeFile(layer.ImagePath)}");
            key.Append(CultureInfo.InvariantCulture, $":{layer.TranslateXMm:R}:{layer.TranslateYMm:R}:{layer.RotationDeg:R}:{layer.Scale:R}:{layer.Visible}:{layer.Opacity:R}");
        }

        return key.ToString();
    }
}
