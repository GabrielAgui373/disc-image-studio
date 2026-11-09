using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>What the user decided about a calibration result.</summary>
public sealed record CalibrationDecision(
    [property: JsonPropertyName("selectedCandidate")] int? SelectedCandidate,
    [property: JsonPropertyName("curveConfirmed")] bool CurveConfirmed,
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("appliedUtc")] DateTimeOffset? AppliedUtc,
    [property: JsonPropertyName("savedPreset")] string? SavedPreset);

/// <summary>Everything a report is built from; the builder decides what goes in.</summary>
public sealed record CalibrationReportInput(
    CalibrationSession Session,
    CalibrationPhotoStep Photo,
    CalibrationFitRequest? FitRequest,
    CalibrationFitOutcome? Fit,
    CalibrationPrediction? Prediction,
    CalibrationDecision Decision,
    string? SessionPath,
    string? ProjectPath);

/// <summary>
/// An auditable calibration report (Task 12.5): the medium and target, the algorithm and its
/// settings, the photo fit, every curve point, every candidate with its residuals, and what the
/// user decided. By default it carries neither the photo nor any file path, so it can be shared
/// as it is; <c>includePersonal</c> adds both on explicit request.
/// </summary>
public static class CalibrationReport
{
    public const string FormatName = "disc-image-studio-calibration-report-v1";

    /// <summary>Algorithm identity: bump when the fitters or the acceptance rules change.</summary>
    public const string AlgorithmVersion = "cross-fitter-1 (CalibrationCrossFitter + CalibrationPhotoRectifier, auto-range 0.02/0.1/0.5/2/5 mm)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToJson(CalibrationReportInput input, DateTimeOffset generatedUtc, bool includePersonal)
    {
        ArgumentNullException.ThrowIfNull(input);
        CalibrationSession session = input.Session;
        CalibrationPhotoStep photo = input.Photo;
        CalibrationRectification? rectification = photo.Rectification;
        IReadOnlyList<CalibrationPoint> points = photo.TracePoints;
        HashSet<int> outliers = [.. input.Prediction?.OutlierIndices ?? []];
        var report = new
        {
            format = FormatName,
            generatedUtc,
            generator = GeneratorName(),
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
            },
            algorithm = new
            {
                version = AlgorithmVersion,
                targetSchemaVersion = session.Target.SchemaVersion,
                acceptance = new { maximumRmsErrorMm = CalibrationMediaRules.MaximumRmsErrorMm, maximumPointErrorMm = CalibrationMediaRules.MaximumPointErrorMm },
                fitOptions = input.FitRequest is { } request ? new
                {
                    request.Options.RadiusRangeMm,
                    request.AutoRange,
                    request.Options.FitDvdPitch,
                    request.Options.HuberDeltaMm,
                    request.Options.MaxCandidates,
                    request.Options.PitchCoefficientRange,
                    request.Options.AdaptiveRobustScale,
                } : null,
            },
            media = new
            {
                kind = session.Target.Parameters.Kind,
                targetId = session.Target.Id,
                generation = Parameters(session.Target.Parameters),
                cdInterleave = session.Target.Parameters.Kind == CalibrationDiscKind.Cd ? session.CdInterleave : (bool?)null,
            },
            photo = photo.Photo is { } image ? new
            {
                workingWidth = image.Width,
                workingHeight = image.Height,
                originalWidth = image.FromSession ? (int?)null : image.OriginalWidth,
                originalHeight = image.FromSession ? (int?)null : image.OriginalHeight,
                physicalOuterRadiusMm = photo.PhysicalOuterRadiusMm,
                holeRadiusMm = CalibrationPhotoLoader.HoleRadiusMm,
                outerEdgePixels = photo.Outer.Select(Pair).ToArray(),
                holeEdgePixels = photo.Hole.Select(Pair).ToArray(),
                rectification = rectification is null ? null : new
                {
                    outerRmsPixels = rectification.Fit.OuterRmsPixels,
                    holeRmsPixels = rectification.Fit.HoleRmsPixels,
                    discToPhotoMatrix9 = rectification.Fit.Transform?.DiscToSourceMatrix9,
                },
                rectificationConfirmed = photo.Confirmed,
                imagePngBase64 = includePersonal ? photo.ToSession()?.ImagePngBase64 : null,
            } : null,
            curve = new
            {
                pointCount = points.Count,
                pointsMm = points.Select((point, index) => new
                {
                    index,
                    xMm = point.X,
                    yMm = point.Y,
                    radiusMm = Math.Sqrt((point.X * point.X) + (point.Y * point.Y)),
                    flagged = outliers.Contains(index),
                }).ToArray(),
            },
            fit = input.Fit is { } fit ? new
            {
                succeeded = fit.Result.Succeeded,
                radiusRangeMm = fit.RadiusRangeMm,
                rangeExpanded = fit.RangeExpanded,
                boundaryLimited = fit.Result.BoundaryLimited,
                warnings = fit.Result.Warnings.Select(warning => new { code = CalibrationFitMessages.Code(warning), text = warning }).ToArray(),
                candidates = fit.Candidates.Select(candidate => new
                {
                    rank = candidate.Rank,
                    parameters = Parameters(candidate.Fit.Parameters),
                    rotationRadians = candidate.Fit.RotationRadians,
                    rmsErrorMm = candidate.Fit.RmsErrorMm,
                    maxErrorMm = candidate.Fit.MaxErrorMm,
                    outlierCount = candidate.Fit.OutlierCount,
                    residualAccepted = candidate.ResidualAccepted,
                }).ToArray(),
                prediction = input.Prediction is { } prediction ? new
                {
                    outlierThresholdMm = prediction.OutlierThresholdMm,
                    outlierIndices = prediction.OutlierIndices,
                } : null,
            } : null,
            decision = input.Decision,
            files = includePersonal ? new { session = input.SessionPath, project = input.ProjectPath } : null,
        };
        return JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine;
    }

    /// <summary>Writes the report atomically.</summary>
    public static void Save(string path, CalibrationReportInput input, DateTimeOffset generatedUtc, bool includePersonal)
        => CalibrationSessionStore.WriteAtomically(path, Encoding.UTF8.GetBytes(ToJson(input, generatedUtc, includePersonal)), privateFile: includePersonal);

    private static object Parameters(CalibrationParameters p) => new
    {
        innerRadiusMm = p.InnerRadiusMm,
        outerRadiusMm = p.OuterRadiusMm,
        sectors = p.Sectors,
        linearDensity = p.LinearDensity,
        pitchLinear = p.Kind == CalibrationDiscKind.Dvd ? p.PitchLinear : (double?)null,
        pitchQuadratic = p.Kind == CalibrationDiscKind.Dvd ? p.PitchQuadratic : (double?)null,
        pitchCubic = p.Kind == CalibrationDiscKind.Dvd ? p.PitchCubic : (double?)null,
        bluRayInvertMapping = p.Kind == CalibrationDiscKind.BluRay ? p.BluRayInvertMapping : (bool?)null,
        trackLengthMm = p.TrackLengthMm,
    };

    private static double[] Pair(CalibrationPoint point) => [point.X, point.Y];

    private static string GeneratorName()
    {
        Assembly assembly = typeof(CalibrationReport).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        return $"DiscImageStudio.Application {version}";
    }
}
