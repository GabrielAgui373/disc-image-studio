using DiscImageStudio.Core.Calibration;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Application.Calibration;

public enum CalibrationWorkStage
{
    Rectifying,
    Resampling,
    Fitting,
    Predicting,
}

/// <summary>
/// Progress of calibration work. While fitting, <see cref="Attempt"/> of
/// <see cref="Attempts"/> is the search box being tried (automatic widening may try several).
/// </summary>
public sealed record CalibrationWorkProgress(CalibrationWorkStage Stage, int Attempt, int Attempts, double? RadiusRangeMm);

/// <param name="AutoRange">Widen the radius box until the optimum is inside it (the WPF default).</param>
public sealed record CalibrationFitRequest(
    CalibrationTarget Target,
    IReadOnlyList<CalibrationCrossTrace> Traces,
    CalibrationFitOptions Options,
    bool AutoRange = true);

/// <summary>A fitted candidate and whether its residual passes the acceptance bounds.</summary>
public sealed record CalibrationCandidate(int Rank, CalibrationFitCandidate Fit, bool ResidualAccepted);

/// <param name="RadiusRangeMm">The box the published result came from.</param>
/// <param name="RangeExpanded">Automatic widening went past the requested box.</param>
public sealed record CalibrationFitOutcome(
    CalibrationFitResult Result,
    double RadiusRangeMm,
    bool RangeExpanded,
    IReadOnlyList<CalibrationCandidate> Candidates);

/// <summary>The fitted photo transform, the rectified view and the fitted edges drawn on the photo.</summary>
public sealed record CalibrationRectification(
    CalibrationPhotoFitResult Fit,
    BgraRaster Rectified,
    double CanvasRadiusMm,
    IReadOnlyList<IReadOnlyList<CalibrationPoint>> FittedEdges);

/// <summary>A candidate's predicted curve over the traced radii, and the traced points visibly off it.</summary>
public sealed record CalibrationPrediction(
    IReadOnlyList<CalibrationPoint> Curve,
    IReadOnlyList<int> OutlierIndices,
    double OutlierThresholdMm);

/// <summary>
/// What differs between media when fitting. One fitter (<see cref="CalibrationCrossFitter"/>)
/// serves every disc; the media decide which terms are free and when a result is good enough
/// to stop widening the search.
/// </summary>
public static class CalibrationMediaRules
{
    /// <summary>Boxes tried by automatic widening, after the requested one.</summary>
    public static IReadOnlyList<double> AutoRanges { get; } = [0.1, 0.5, 2, 5];

    /// <summary>Mean curve distance above which a candidate is not applicable, in mm.</summary>
    public const double MaximumRmsErrorMm = 0.35;

    /// <summary>Largest single-point distance above which a candidate is not applicable, in mm.</summary>
    public const double MaximumPointErrorMm = 0.8;

    /// <summary>Only DVD has free track-pitch terms; the other media ignore the option.</summary>
    public static CalibrationFitOptions Normalize(CalibrationDiscKind kind, CalibrationFitOptions options)
        => kind == CalibrationDiscKind.Dvd ? options : options with { FitDvdPitch = false };

    /// <summary>
    /// Blu-ray's observed curve continues beyond the drawing band, so a narrow box can return an
    /// interior optimum with a visibly unusable residual; keep widening while it is that poor.
    /// </summary>
    public static bool KeepWidening(CalibrationDiscKind kind, CalibrationFitResult result)
        => kind == CalibrationDiscKind.BluRay && result.RmsErrorMm > MaximumPointErrorMm;

    public static bool ResidualAccepted(CalibrationFitCandidate candidate)
        => double.IsFinite(candidate.RmsErrorMm) && double.IsFinite(candidate.MaxErrorMm)
           && candidate.RmsErrorMm <= MaximumRmsErrorMm && candidate.MaxErrorMm <= MaximumPointErrorMm;

    /// <summary>
    /// Failures caused by the traced points themselves; a wider box cannot fix them. The Core
    /// fitter reports them as text, so they are recognised by its fixed messages.
    /// </summary>
    public static bool IsInputFailure(CalibrationFitResult result)
        => !result.Succeeded && result.Warnings.Any(warning =>
            warning.Contains("至少标出", StringComparison.Ordinal)
            || warning.Contains("覆盖范围太窄", StringComparison.Ordinal)
            || warning.Contains("向内回退", StringComparison.Ordinal)
            || warning.Contains("接近半圈", StringComparison.Ordinal)
            || warning.Contains("转角过大", StringComparison.Ordinal));
}

/// <summary>
/// Runs the calibration fitters off the UI thread with progress and cancellation: photo
/// rectification, curve fitting with automatic widening of the search box, and the candidate
/// prediction. Each kind of work is a channel where the latest request wins: starting one
/// cancels the previous request of the same kind, which then ends in
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class CalibrationFitCoordinator : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<CalibrationWorkStage, CancellationTokenSource> _running = [];
    private bool _disposed;

    public Task<CalibrationRectification> RectifyAsync(
        CalibrationPhoto photo,
        IReadOnlyList<CalibrationPoint> outerEdge,
        IReadOnlyList<CalibrationPoint> holeEdge,
        double physicalOuterRadiusMm,
        int pixelSize = 1400,
        IProgress<CalibrationWorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        CalibrationPoint[] outer = [.. outerEdge ?? throw new ArgumentNullException(nameof(outerEdge))];
        CalibrationPoint[] hole = [.. holeEdge ?? throw new ArgumentNullException(nameof(holeEdge))];
        return RunAsync(CalibrationWorkStage.Rectifying, cancellationToken, token =>
        {
            progress?.Report(new(CalibrationWorkStage.Rectifying, 1, 1, null));
            CalibrationPhotoFitResult fit = CalibrationPhotoRectifier.Fit(
                outer, hole, physicalOuterRadiusMm, CalibrationPhotoLoader.HoleRadiusMm, token);
            if (!fit.Succeeded || fit.Transform is null)
            {
                throw new CalibrationException("calibration.photo.rectifyFailed", fit.Message);
            }

            token.ThrowIfCancellationRequested();
            progress?.Report(new(CalibrationWorkStage.Resampling, 1, 1, null));
            // The WPF wizard shows 3 mm of margin around the physical edge.
            double canvasRadius = physicalOuterRadiusMm + 3;
            BgraRaster rectified = CalibrationPhotoLoader.Rectify(photo, fit.Transform, canvasRadius, pixelSize, token);
            return new CalibrationRectification(
                fit,
                rectified,
                canvasRadius,
                CalibrationPhotoLoader.BoundaryContours(fit.Transform, physicalOuterRadiusMm));
        });
    }

    public Task<CalibrationFitOutcome> FitAsync(
        CalibrationFitRequest request,
        IProgress<CalibrationWorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationCrossTrace[] traces = [.. request.Traces];
        return RunAsync(CalibrationWorkStage.Fitting, cancellationToken, token => Fit(request with { Traces = traces }, progress, token));
    }

    /// <summary>
    /// The candidate's curve over the traced radii (plus the outlier threshold at both ends)
    /// and the indices (into
    /// <paramref name="tracePoints"/>) of points farther from it than a small multiple of the
    /// mean residual: those are misplaced clicks, not noise.
    /// </summary>
    public Task<CalibrationPrediction> PredictAsync(
        CalibrationTarget target,
        CalibrationFitCandidate candidate,
        IReadOnlyList<CalibrationPoint> tracePoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        CalibrationPoint[] points = [.. tracePoints ?? throw new ArgumentNullException(nameof(tracePoints))];
        return RunAsync(CalibrationWorkStage.Predicting, cancellationToken, token =>
        {
            double[] radii = points.Select(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y))).ToArray();
            if (radii.Length < 2 || !(radii.Max() > radii.Min()))
            {
                throw new CalibrationException("calibration.fit.tooFewPoints", "The prediction needs points at two different radii.");
            }

            double threshold = Math.Max(0.2, candidate.RmsErrorMm * 2.5);
            // On DVD and Blu-ray the curve runs almost along the circumference, so rotation and
            // radius nearly trade off and the fitted curve can sit slid along itself. Cut exactly
            // at the traced radii it would then stop short of the end points and flag them,
            // which the WPF wizard did; the curve is extended by the outlier threshold instead.
            double inner = Math.Max(radii.Min() - threshold, radii.Min() / 2);
            IReadOnlyList<CalibrationPoint> curve = CalibrationCrossPrediction.GetOutline(
                target, candidate.Parameters, inner, radii.Max() + threshold, candidate.RotationRadians, cancellationToken: token);
            int[] outliers = Enumerable.Range(0, points.Length)
                .Where(index => DistanceToPolyline(points[index], curve) > threshold)
                .ToArray();
            return new CalibrationPrediction(curve, outliers, threshold);
        });
    }

    /// <summary>Cancels every running request.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (CancellationTokenSource source in _running.Values)
            {
                source.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (CancellationTokenSource source in _running.Values)
            {
                source.Cancel();
            }
        }
    }

    /// <summary>The automatic-widening fit, synchronous; exposed for the CLI and checks.</summary>
    public static CalibrationFitOutcome Fit(
        CalibrationFitRequest request,
        IProgress<CalibrationWorkProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CalibrationTarget target = request.Target;
        CalibrationDiscKind kind = target.Parameters.Kind;
        CalibrationFitOptions options = CalibrationMediaRules.Normalize(kind, request.Options);
        double requested = options.RadiusRangeMm;
        double[] ranges = request.AutoRange
            ? [.. CalibrationMediaRules.AutoRanges.Append(requested).Where(range => range >= requested).Distinct().Order()]
            : [requested];

        CalibrationFitResult? best = null;
        CalibrationFitResult? last = null;
        double bestRange = requested;
        for (int attempt = 0; attempt < ranges.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double range = ranges[attempt];
            progress?.Report(new(CalibrationWorkStage.Fitting, attempt + 1, ranges.Length, range));
            CalibrationFitResult result = CalibrationCrossFitter.Fit(target, request.Traces, options with { RadiusRangeMm = range }, cancellationToken);
            last = result;
            if (!request.AutoRange
                || (result.Succeeded && !result.BoundaryLimited && !CalibrationMediaRules.KeepWidening(kind, result)))
            {
                return Outcome(result, range, range > requested);
            }

            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }

            if (CalibrationMediaRules.IsInputFailure(result))
            {
                break;
            }
        }

        // A boundary-clamped answer is published only when no wider box gave an interior one.
        return best is not null
            ? Outcome(best, bestRange, bestRange > requested)
            : Outcome(last!, requested, expanded: false);
    }

    private static CalibrationFitOutcome Outcome(CalibrationFitResult result, double range, bool expanded)
        => new(
            result,
            range,
            expanded,
            result.Candidates
                .Select((candidate, index) => new CalibrationCandidate(index + 1, candidate, CalibrationMediaRules.ResidualAccepted(candidate)))
                .ToArray());

    private async Task<T> RunAsync<T>(CalibrationWorkStage channel, CancellationToken cancellationToken, Func<CancellationToken, T> work)
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running.Remove(channel, out CancellationTokenSource? previous))
            {
                previous.Cancel();
            }

            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[channel] = source;
        }

        try
        {
            CancellationToken token = source.Token;
            return await Task.Run(() => work(token), token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(channel, out CancellationTokenSource? current) && ReferenceEquals(current, source))
                {
                    _running.Remove(channel);
                }
            }

            source.Dispose();
        }
    }

    private static double DistanceToPolyline(CalibrationPoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].X;
            double ay = line[i - 1].Y;
            double dx = line[i].X - ax;
            double dy = line[i].Y - ay;
            double t = Math.Clamp((((point.X - ax) * dx) + ((point.Y - ay) * dy)) / Math.Max(1e-30, (dx * dx) + (dy * dy)), 0, 1);
            double ex = point.X - ax - (t * dx);
            double ey = point.Y - ay - (t * dy);
            best = Math.Min(best, (ex * ex) + (ey * ey));
        }

        return Math.Sqrt(best);
    }
}
