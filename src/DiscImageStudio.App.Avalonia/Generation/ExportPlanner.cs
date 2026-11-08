using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Generation;
using DiscImageStudio.Preview;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.App.Avalonia.Generation;

/// <summary>
/// What the Export page shows before anything is generated: the generation plan of the current
/// project, the destination checks and the state of the design source. Exactly one of
/// <see cref="Plan"/> and <see cref="PlanError"/> is set.
/// </summary>
public sealed record ExportReview(
    string TargetPath,
    GenerationPlan? Plan,
    GenerationValidationError? PlanError,
    OutputPreflight? Preflight,
    int PaintedLayers,
    int VisibleLayers,
    IReadOnlyList<CompositionIssue> SourceIssues,
    string? DataFingerprint = null);

/// <summary>
/// Plans an export without generating it. The plan does not depend on the picture's pixels (sizes,
/// layout and drawing range come from geometry, tone and the data folder), so it reads the small
/// draft composition the preview cache already keeps instead of composing 8192 px; the export run
/// (Task 11.3) plans again with the full generation source.
/// </summary>
public sealed class ExportPlanner(PreviewSourceCache sources, IDiscGenerationService generation)
{
    private readonly PreviewSourceCache _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly IDiscGenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    public async Task<ExportReview> ReviewAsync(
        ProjectWorkspace workspace,
        string? projectFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string target = ArtifactPreviewService.ArtifactPath(workspace, projectFilePath) ?? string.Empty;
        int visible = workspace.Design.Layers.Count(layer => layer.Visible);
        string? dataDirectory = string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory) ? null : workspace.Delivery.DataDirectory.Trim();
        string? fingerprint = await Task.Run(() => DataFolderFingerprint.Compute(dataDirectory), cancellationToken).ConfigureAwait(false);
        using PreviewSourceLease source = await _sources
            .AcquireAsync(workspace, projectFilePath, PreviewDefaults.MinimumSourceSize, cancellationToken)
            .ConfigureAwait(false);
        GenerationPlan plan;
        try
        {
            GenerateDiscRequest request = ProjectGenerationRequestFactory.Create(workspace, source.Path, target);
            plan = await _generation.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GenerationValidationException exception)
        {
            return new ExportReview(target, null, exception.Error, null, source.PaintedLayerCount, visible, source.Issues, fingerprint);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            GenerationValidationError error = new(
                "generation.plan.invalid",
                "plan",
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = exception.Message });
            return new ExportReview(target, null, error, null, source.PaintedLayerCount, visible, source.Issues, fingerprint);
        }

        OutputPreflight preflight = await Task
            .Run(() => _generation.Preflight(plan, target, ArtifactReplacePolicy.Fail), cancellationToken)
            .ConfigureAwait(false);
        return new ExportReview(target, plan, null, preflight, source.PaintedLayerCount, visible, source.Issues, fingerprint);
    }
}
