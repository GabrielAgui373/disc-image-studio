using System.Diagnostics;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.Generation;

/// <param name="TargetPath">The target file (<c>.iso</c>, or <c>.wav</c>/<c>.raw</c> for CD).</param>
public sealed record CalibrationTargetRunRequest(
    CalibrationSession Session,
    CdArtifactFormat CdFormat,
    string TargetPath,
    bool ReplaceExisting,
    bool VerifyHash);

/// <param name="SessionPath">The published <c>.calibration.json</c> snapshot.</param>
public sealed record CalibrationTargetOutcome(
    GenerationPlan Plan,
    PublishedArtifact Artifact,
    ArtifactValidationReport Validation,
    string SessionPath,
    TimeSpan Elapsed);

/// <summary>
/// Plans and checks a calibration target (Task 12.2) and publishes it through the transactional
/// publisher with its session snapshot, validated before the swap like an export.
/// </summary>
public sealed class CalibrationTargetRunner(IDiscGenerationService generation)
{
    private readonly IDiscGenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    public Task<GenerationPlan> PlanAsync(CalibrationSession session, CdArtifactFormat cdFormat, CancellationToken cancellationToken)
        => _generation.PlanAsync(new GenerateCalibrationTargetRequest(session, cdFormat), cancellationToken);

    public OutputPreflight Preflight(GenerationPlan plan, string targetPath, ArtifactReplacePolicy policy)
        => _generation.Preflight(plan, targetPath, policy);

    public async Task<CalibrationTargetOutcome> RunAsync(
        CalibrationTargetRunRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch clock = Stopwatch.StartNew();
        progress?.Report(new ExportProgress(ExportStage.Planning, null, 0, 0, 0, clock.Elapsed, null));
        GenerationPlan plan = await PlanAsync(request.Session, request.CdFormat, cancellationToken).ConfigureAwait(false);
        ArtifactReplacePolicy policy = request.ReplaceExisting ? ArtifactReplacePolicy.Replace : ArtifactReplacePolicy.Fail;
        OutputPreflight preflight = await Task.Run(() => Preflight(plan, request.TargetPath, policy), cancellationToken).ConfigureAwait(false);
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
                validation = ArtifactValidator.Validate(set, request.VerifyHash, token);
                if (!validation.Passed)
                {
                    throw new ArtifactValidationException(validation);
                }
            });
        PublishedArtifact artifact = await _generation
            .PublishFileAsync(plan, preflight.TargetPath, options, new ExportRunner.WriteProgress(progress, clock, plan.EstimatedBytes), cancellationToken)
            .ConfigureAwait(false);
        string sessionPath = Path.ChangeExtension(artifact.Artifact.Path, DiscGenerationService.CalibrationSidecarSuffix);
        return new CalibrationTargetOutcome(plan, artifact, validation!, sessionPath, clock.Elapsed);
    }
}
