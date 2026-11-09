using System.Diagnostics;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Generation;
using DiscImageStudio.Preview;

namespace DiscImageStudio.App.Avalonia.Generation;

public enum ExportStage
{
    Composing,
    Planning,
    Writing,
    Sidecars,
    Validating,
    Publishing,
}

/// <summary>Progress of an export run; byte figures and rates are set while writing.</summary>
public sealed record ExportProgress(
    ExportStage Stage,
    double? Fraction,
    long CompletedBytes,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining);

/// <param name="ExpectedDataFingerprint">
/// The data folder's fingerprint when the user reviewed the plan; a different folder content at
/// run time stops the export before anything is written.
/// </param>
public sealed record ExportRunRequest(
    ProjectWorkspace Workspace,
    string? ProjectFilePath,
    bool ReplaceExisting,
    bool VerifyHash,
    string? ExpectedDataFingerprint);

public sealed record ExportOutcome(
    GenerationPlan Plan,
    PublishedArtifact Artifact,
    ArtifactValidationReport Validation,
    TimeSpan Elapsed);

/// <summary>The data folder changed after the plan was reviewed or while the image was written.</summary>
public sealed class ExportSourceChangedException(string message) : IOException(message);

/// <summary>
/// One export run (Task 11.3): compose the 8192 px generation source, plan again with it, check
/// the destination again, then publish through the transactional publisher with validation and
/// a data-folder check just before the swap (Task 11.4). Cancelable at every stage; a canceled or
/// failed run never leaves a new file at the destination.
/// </summary>
public sealed class ExportRunner(ProjectGenerationPreparer preparer, IDiscGenerationService generation)
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaximumRemaining = TimeSpan.FromHours(99);

    private readonly ProjectGenerationPreparer _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
    private readonly IDiscGenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    public async Task<ExportOutcome> RunAsync(
        ExportRunRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch clock = Stopwatch.StartNew();
        ProjectWorkspace workspace = request.Workspace;
        string target = ArtifactPreviewService.ArtifactPath(workspace, request.ProjectFilePath)
                        ?? throw new GenerationValidationException(new GenerationValidationError(
                            "generation.output.required", "outputPath", new Dictionary<string, object?>()));
        string? dataDirectory = string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory) ? null : workspace.Delivery.DataDirectory.Trim();

        string? fingerprint = await Task.Run(() => DataFolderFingerprint.Compute(dataDirectory), cancellationToken).ConfigureAwait(false);
        if (request.ExpectedDataFingerprint is { } expected && expected != fingerprint)
        {
            throw new ExportSourceChangedException("The data folder changed after the plan was reviewed.");
        }

        Report(progress, ExportStage.Composing, clock);
        using PreparedGeneration prepared = await _preparer
            .PrepareAsync(workspace, request.ProjectFilePath, target, directBurn: false, cancellationToken)
            .ConfigureAwait(false);

        Report(progress, ExportStage.Planning, clock);
        GenerationPlan plan = await _generation.PlanAsync(prepared.Request, cancellationToken).ConfigureAwait(false);
        ArtifactReplacePolicy policy = request.ReplaceExisting ? ArtifactReplacePolicy.Replace : ArtifactReplacePolicy.Fail;
        OutputPreflight preflight = await Task.Run(() => _generation.Preflight(plan, target, policy), cancellationToken).ConfigureAwait(false);
        if (preflight.Problems.Count > 0)
        {
            throw new GenerationValidationException(preflight.Problems[0]);
        }

        ArtifactValidationReport? validation = null;
        ArtifactPublishOptions options = new(
            policy,
            WriteManifest: true,
            BeforeCommit: (set, token) =>
            {
                if (DataFolderFingerprint.Compute(dataDirectory) != fingerprint)
                {
                    throw new ExportSourceChangedException("The data folder changed while the image was written.");
                }

                validation = ArtifactValidator.Validate(set, request.VerifyHash, token);
                if (!validation.Passed)
                {
                    throw new ArtifactValidationException(validation);
                }
            });

        WriteProgress writing = new(progress, clock, plan.EstimatedBytes);
        PublishedArtifact artifact = await _generation
            .PublishFileAsync(plan, target, options, writing, cancellationToken)
            .ConfigureAwait(false);
        return new ExportOutcome(plan, artifact, validation!, clock.Elapsed);
    }

    private static void Report(IProgress<ExportProgress>? progress, ExportStage stage, Stopwatch clock)
        => progress?.Report(new ExportProgress(stage, null, 0, 0, 0, clock.Elapsed, null));

    /// <summary>
    /// Turns the pipeline's byte progress into stage, rate and time left, at most a few times a
    /// second so the UI is not flooded by a fast writer.
    /// </summary>
    internal sealed class WriteProgress(IProgress<ExportProgress>? target, Stopwatch clock, long total) : IProgress<GenerationProgress>
    {
        private readonly Stopwatch _sinceWrite = new();
        private TimeSpan? _lastReport;

        public void Report(GenerationProgress value)
        {
            ExportStage stage = value.Stage switch
            {
                "generation.sidecars" => ExportStage.Sidecars,
                "generation.validate" => ExportStage.Validating,
                "generation.publish" => ExportStage.Publishing,
                _ => ExportStage.Writing,
            };
            if (stage == ExportStage.Writing)
            {
                if (!_sinceWrite.IsRunning)
                {
                    _sinceWrite.Start();
                }

                bool done = value.CompletedBytes >= total;
                if (!done && _lastReport is { } last && clock.Elapsed - last < ReportInterval)
                {
                    return;
                }
            }

            _lastReport = clock.Elapsed;
            double seconds = _sinceWrite.Elapsed.TotalSeconds;
            double rate = seconds > 0.2 ? value.CompletedBytes / seconds : 0;
            // Only a meaningful rate gives a meaningful estimate; a tiny early rate would put the
            // estimate beyond what TimeSpan holds.
            TimeSpan? remaining = stage == ExportStage.Writing && rate >= 64 * 1024
                ? TimeSpan.FromSeconds(Math.Min((total - value.CompletedBytes) / rate, MaximumRemaining.TotalSeconds))
                : null;
            double? fraction = stage == ExportStage.Writing && total > 0 ? Math.Clamp((double)value.CompletedBytes / total, 0, 1) : null;
            target?.Report(new ExportProgress(stage, fraction, value.CompletedBytes, total, rate, clock.Elapsed, remaining));
        }
    }
}
