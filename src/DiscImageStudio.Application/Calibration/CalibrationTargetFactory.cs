using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>
/// Builds a calibration target from a disc geometry (the project's or a preset's), with the
/// conversions the WPF wizard used: the standard CD velocity, the standard DVD channel-bit
/// length with the cubic pitch terms, and Blu-ray drawn over the whole layer.
/// </summary>
public static class CalibrationTargetFactory
{
    /// <summary>Standard CD linear velocity (CdDiscParameters.StandardLinearVelocityMmPerSecond).</summary>
    public const double CdLinearVelocityMmPerSecond = 1200.0;

    /// <summary>Standard DVD channel-bit length (DvdStreamingOptions.StandardChannelBitLengthNm).</summary>
    public const double DvdChannelBitLengthNm = 133.3;

    public static CalibrationParameters ParametersFor(MediaState media)
    {
        ArgumentNullException.ThrowIfNull(media);
        CalibrationParameters parameters = media switch
        {
            CdState cd => new(CalibrationDiscKind.Cd, cd.InnerRadiusMm, cd.OuterRadiusMm, cd.Sectors, CdLinearVelocityMmPerSecond),
            DvdState dvd => new(
                CalibrationDiscKind.Dvd,
                dvd.InnerRadiusMm,
                dvd.OuterRadiusMm,
                dvd.Sectors,
                DvdChannelBitLengthNm,
                dvd.PitchLinear,
                dvd.PitchQuadratic,
                dvd.PitchCubic),
            BluRayState bluRay => new(
                CalibrationDiscKind.BluRay,
                bluRay.InnerRadiusMm,
                bluRay.OuterRadiusMm,
                bluRay.Sectors,
                CalibrationParameters.BluRayChannelBitLengthNm,
                BluRayInvertMapping: bluRay.InvertMapping,
                DrawingStartSector: 0,
                DrawingEndSectorExclusive: bluRay.Sectors),
            _ => throw new ArgumentException("Unsupported media.", nameof(media)),
        };
        try
        {
            parameters.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new CalibrationException("calibration.target.invalidGeometry", exception.Message, exception);
        }

        return parameters;
    }

    /// <summary>
    /// A fresh session for a new target: no photo, no observations, the WPF fit defaults
    /// (search centred on the generation parameters, automatic range, robust scale).
    /// </summary>
    public static CalibrationSession NewSession(MediaState media)
    {
        CalibrationParameters parameters = ParametersFor(media);
        CalibrationTarget target = CalibrationTarget.Create(parameters);
        return new CalibrationSession(
            target,
            [],
            DefaultFitOptions(target),
            CdInterleave: media is not CdState cd || cd.Interleave);
    }

    public static CalibrationFitOptions DefaultFitOptions(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new CalibrationFitOptions(
            SearchCenter: target.Parameters,
            RadiusRangeMm: 0.02,
            FitDvdPitch: true,
            AdaptiveRobustScale: true,
            AutoRange: true);
    }

    /// <summary>The WPF wizard's file name: <c>calibration-&lt;first 8 hex of the id&gt;</c>.</summary>
    public static string FileName(CalibrationTarget target, string extension)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"calibration-{target.Id[..8]}{extension}";
    }
}
