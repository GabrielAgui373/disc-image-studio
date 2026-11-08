using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>
/// Applying a calibration to a project (Task 12.5), as pure workspace edits for the undo history.
/// Like the WPF wizard, applying sets the generation geometry to the measured one, so the next
/// image lands where it was designed, and it also records the measurement, which Preview reads
/// discs with. Nothing changes until the user applies; undo restores both.
/// </summary>
public static class CalibrationEdits
{
    /// <summary>The fitted parameters as generation geometry of the project's family.</summary>
    public static MediaState FittedMedia(MediaState current, CalibrationParameters fitted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fitted);
        return (current, fitted.Kind) switch
        {
            (CdState cd, CalibrationDiscKind.Cd) => new CdState(fitted.InnerRadiusMm, fitted.OuterRadiusMm, fitted.Sectors, cd.WriteCue, cd.Interleave),
            (DvdState, CalibrationDiscKind.Dvd) => new DvdState(
                fitted.InnerRadiusMm, fitted.OuterRadiusMm, fitted.Sectors, fitted.PitchLinear, fitted.PitchQuadratic, fitted.PitchCubic),
            (BluRayState, CalibrationDiscKind.BluRay) => new BluRayState(
                fitted.InnerRadiusMm, fitted.OuterRadiusMm, fitted.Sectors, fitted.BluRayInvertMapping),
            _ => throw new CalibrationException(
                "calibration.apply.familyMismatch",
                "The calibration measured another disc family than the project's."),
        };
    }

    public static ProjectWorkspace ApplyMeasurement(
        ProjectWorkspace workspace,
        string targetId,
        CalibrationFitCandidate candidate,
        DateTimeOffset measuredUtc)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(candidate);
        CalibrationParameters p = candidate.Parameters;
        MediaState media = FittedMedia(workspace.Media, p);
        return workspace with
        {
            Media = media,
            Measurement = new CalibrationMeasurement(
                p.InnerRadiusMm,
                p.OuterRadiusMm,
                p.Sectors,
                p.PitchLinear,
                p.PitchQuadratic,
                p.PitchCubic,
                targetId,
                measuredUtc,
                candidate.RmsErrorMm,
                candidate.MaxErrorMm),
        };
    }

    /// <summary>Forgets the measurement; the generation geometry stays as it is.</summary>
    public static ProjectWorkspace ClearMeasurement(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace with { Measurement = null };
    }
}
