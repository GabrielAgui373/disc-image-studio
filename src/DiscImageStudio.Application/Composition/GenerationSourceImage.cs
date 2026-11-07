using DiscImageStudio.Rendering;

namespace DiscImageStudio.Application.Composition;

/// <summary>
/// A composed 8192 px PNG prepared for one generation run and handed to the typed pipeline as
/// <c>SourceImagePath</c>. It owns the file: disposing deletes it. The pixel buffer is not kept,
/// so holding this object costs no memory beyond the metadata.
/// </summary>
public sealed class GenerationSourceImage : IDisposable
{
    internal GenerationSourceImage(
        string path,
        int size,
        int paintedLayerCount,
        IReadOnlyList<CompositionIssue> issues)
    {
        Path = path;
        Size = size;
        PaintedLayerCount = paintedLayerCount;
        Issues = issues;
    }

    public string Path { get; }

    public int Size { get; }

    public int PaintedLayerCount { get; }

    /// <summary>Layers that could not be painted; callers decide whether to warn or stop.</summary>
    public IReadOnlyList<CompositionIssue> Issues { get; }

    public bool IsEmpty => PaintedLayerCount == 0;

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the host's cache cleanup removes it later.
        }
    }
}
