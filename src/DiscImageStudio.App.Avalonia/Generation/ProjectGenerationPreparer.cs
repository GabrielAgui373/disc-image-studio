using DiscImageStudio.Application.Composition;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.Generation;

/// <summary>
/// A generation request together with the composed source image it reads. Disposing deletes the
/// temporary source image; keep it alive until generation or burning has finished.
/// </summary>
public sealed class PreparedGeneration(GenerateDiscRequest request, GenerationSourceImage source) : IDisposable
{
    public GenerateDiscRequest Request { get; } = request;

    public GenerationSourceImage Source { get; } = source;

    public void Dispose() => Source.Dispose();
}

/// <summary>
/// Entry point that Export (Task 11) and Burn (Task 13) use to obtain what the typed pipeline
/// needs: the project composed at 8192 px, written transactionally to the cache folder, and the
/// request that points at it. Both destinations therefore consume the same composition rules.
/// </summary>
public sealed class ProjectGenerationPreparer(ProjectCompositionService compositions, string temporaryDirectory)
{
    private readonly ProjectCompositionService _compositions =
        compositions ?? throw new ArgumentNullException(nameof(compositions));

    public string TemporaryDirectory { get; } = string.IsNullOrWhiteSpace(temporaryDirectory)
        ? throw new ArgumentException("A temporary directory is required.", nameof(temporaryDirectory))
        : temporaryDirectory;

    public async Task<PreparedGeneration> PrepareAsync(
        ProjectWorkspace workspace,
        string? projectFilePath,
        string? outputPath = null,
        bool directBurn = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        GenerationSourceImage source = await _compositions.PrepareGenerationSourceAsync(
            workspace,
            projectFilePath,
            TemporaryDirectory,
            cancellationToken);
        try
        {
            return new PreparedGeneration(
                ProjectGenerationRequestFactory.Create(workspace, source.Path, outputPath, directBurn),
                source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }
}
