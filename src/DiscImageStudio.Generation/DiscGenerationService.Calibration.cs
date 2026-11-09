using System.Diagnostics;
using System.Text;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Generation;

/// <summary>
/// Calibration targets (Task 12.2). The engines, their parameters and the byte order are the
/// WPF wizard's, so a target generated here is byte for byte the one the WPF host writes; what
/// changes is the publication, which goes through the transactional publisher with the
/// <c>.calibration.json</c> snapshot as a companion.
/// </summary>
public sealed partial class DiscGenerationService
{
    /// <summary>Kind and suffix of the session snapshot published beside a target.</summary>
    public const string CalibrationSidecarKind = "calibration";

    public const string CalibrationSidecarSuffix = ".calibration.json";

    private static GenerationPlan PlanCalibrationTarget(GenerateCalibrationTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Session);
        try
        {
            request.Session.Validate();
        }
        catch (ArgumentException exception)
        {
            throw Validation("generation.calibration.session_invalid", "session", exception, ("reason", exception.Message));
        }

        CalibrationParameters parameters = request.Session.Target.Parameters;
        GenerationSidecar snapshot = new(CalibrationSidecarKind, CalibrationSidecarSuffix, true);
        switch (parameters.Kind)
        {
            case CalibrationDiscKind.Cd:
            {
                if (request.CdFormat is not (CdArtifactFormat.WaveAudio or CdArtifactFormat.RawAudio))
                {
                    throw Validation("generation.calibration.cd_format", nameof(request.CdFormat), ("format", request.CdFormat));
                }

                CdDiscParameters disc = CalibrationCdParameters(parameters);
                disc.Validate();
                bool wave = request.CdFormat == CdArtifactFormat.WaveAudio;
                long bytes = checked(disc.TotalBytes + (wave ? 44 : 0));
                return CalibrationPlan(
                    request,
                    GenerationFilesystem.CdDa,
                    new GenerationRange(0, disc.Sectors),
                    bytes,
                    wave ? [snapshot, new("cue", ".cue", true)] : [snapshot],
                    $"CD calibration target: {disc.Sectors} sectors, {(wave ? "WAVE" : "raw")}, {bytes} bytes");
            }

            case CalibrationDiscKind.BluRay:
            {
                BluRayDiscParameters disc = CalibrationBluRayParameters(parameters);
                (uint start, uint end) = CalibrationBluRayRange(parameters, disc);
                return CalibrationPlan(
                    request,
                    GenerationFilesystem.None,
                    new GenerationRange(start, end),
                    disc.ContentLength,
                    [snapshot],
                    $"Blu-ray calibration target: {disc.TotalSectors} sectors, drawing {start}..{end - 1}, {disc.ContentLength} bytes");
            }

            default:
            {
                DvdStreamingOptions options = CalibrationDvdOptions(parameters);
                options.Validate();
                return CalibrationPlan(
                    request,
                    GenerationFilesystem.None,
                    new GenerationRange(0, options.TotalSectors),
                    options.ContentLength,
                    [snapshot],
                    $"DVD calibration target: {options.TotalSectors} sectors, {options.ContentLength} bytes");
            }
        }
    }

    private static GenerationPlan CalibrationPlan(
        GenerateCalibrationTargetRequest request,
        GenerationFilesystem filesystem,
        GenerationRange range,
        long bytes,
        IReadOnlyList<GenerationSidecar> sidecars,
        string summary)
        => new(
            request,
            request.Family,
            filesystem,
            range,
            bytes,
            0,
            0,
            GenerationCapability.SequentialStream
                | GenerationCapability.MetadataSidecar
                | (sidecars.Any(sidecar => sidecar.Kind == "cue") ? GenerationCapability.CueSheet : GenerationCapability.None),
            sidecars,
            summary,
            new CalibrationTargetPlanDetails(request));

    private static void GenerateCalibrationTarget(
        GenerationPlan plan,
        CalibrationTargetPlanDetails details,
        Stream output,
        IProgress<GenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        GenerateCalibrationTargetRequest request = details.Request;
        CalibrationTarget target = request.Session.Target;
        CalibrationParameters parameters = target.Parameters;
        Stopwatch stopwatch = Stopwatch.StartNew();
        switch (parameters.Kind)
        {
            case CalibrationDiscKind.Cd:
            {
                CdDiscParameters disc = CalibrationCdParameters(parameters);
                bool wave = request.CdFormat == CdArtifactFormat.WaveAudio;
                if (wave)
                {
                    WriteWaveHeader(output, disc.TotalBytes);
                }

                long prefix = wave ? 44 : 0;
                // Same samples and byte order as CdTrackGenerator.GeneratePattern on a .wav/.raw path.
                CdTrackGenerator.GeneratePatternToStream(
                    target.Sample,
                    output,
                    disc,
                    request.Session.CdInterleave,
                    value => progress?.Report(new GenerationProgress(
                        "write",
                        checked(prefix + value.CompletedBytes),
                        plan.EstimatedBytes,
                        value.Elapsed,
                        "generation.calibration.write")),
                    cancellationToken,
                    "calibration-target",
                    wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian);
                break;
            }

            case CalibrationDiscKind.BluRay:
            {
                BluRayDiscParameters disc = CalibrationBluRayParameters(parameters);
                (uint start, uint end) = CalibrationBluRayRange(parameters, disc);
                BluRayPatternRangeMapping mapping = new(
                    disc,
                    target.Sample,
                    start,
                    end,
                    parameters.BluRayInvertMapping,
                    // Targets and the fixed reference pixels are immutable.
                    allowParallelSampling: true);
                InlineProgress<BluRayStreamingProgress>? adapter = progress is null
                    ? null
                    : new(value => progress.Report(new GenerationProgress(
                        "write", value.CompletedBytes, value.TotalBytes, value.Elapsed, "generation.calibration.write")));
                BluRayStreamingGenerator.GenerateToStream(
                    output,
                    new BluRayStreamingOptions(disc, 0, disc.TotalSectors),
                    mapping,
                    adapter,
                    cancellationToken);
                break;
            }

            default:
                DvdStreamingGenerator.GeneratePattern(
                    target.Sample,
                    output,
                    CalibrationDvdOptions(parameters),
                    value => progress?.Report(new GenerationProgress(
                        "write", value.CompletedBytes, value.TotalBytes, value.Elapsed, "generation.calibration.write")),
                    cancellationToken);
                break;
        }

        progress?.Report(new GenerationProgress(
            "complete", plan.EstimatedBytes, plan.EstimatedBytes, stopwatch.Elapsed, "generation.complete"));
    }

    /// <summary>The session snapshot (and, for a WAVE track, the cue sheet) beside the staged target.</summary>
    private static void WriteCalibrationSidecars(CalibrationTargetPlanDetails details, string contentPath, string locationPath)
    {
        string json = CalibrationSessionJson.Serialize(details.Request.Session);
        File.WriteAllText(Path.ChangeExtension(locationPath, CalibrationSidecarSuffix), json, new UTF8Encoding(false));
        if (details.Request.Session.Target.Parameters.Kind == CalibrationDiscKind.Cd
            && details.Request.CdFormat == CdArtifactFormat.WaveAudio)
        {
            File.WriteAllText(
                Path.ChangeExtension(locationPath, ".cue"),
                $"FILE \"{Path.GetFileName(contentPath)}\" WAVE\r\n"
                    + "  TRACK 01 AUDIO\r\n"
                    + "    INDEX 01 00:00:00\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    // The WPF wizard's conversions (CalibrationViewModel.CdParameters/DvdOptions/BluRayParameters).
    private static CdDiscParameters CalibrationCdParameters(CalibrationParameters p)
        => new(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity, 0, p.OuterRadiusMm);

    private static DvdStreamingOptions CalibrationDvdOptions(CalibrationParameters p)
        => new(
            checked((uint)p.Sectors),
            p.InnerRadiusMm,
            p.OuterRadiusMm,
            p.LinearDensity,
            0,
            PitchLinear: p.PitchLinear,
            PitchQuadratic: p.PitchQuadratic,
            PitchCubic: p.PitchCubic);

    private static BluRayDiscParameters CalibrationBluRayParameters(CalibrationParameters p)
    {
        BluRayDiscParameters disc = new(checked((uint)p.Sectors), 1, p.InnerRadiusMm, p.OuterRadiusMm);
        disc.Validate();
        return disc;
    }

    private static (uint Start, uint End) CalibrationBluRayRange(CalibrationParameters p, BluRayDiscParameters disc)
        => (checked((uint)p.DrawingStartSector),
            p.DrawingEndSectorExclusive == 0 ? disc.TotalSectors : checked((uint)p.DrawingEndSectorExclusive));
}
