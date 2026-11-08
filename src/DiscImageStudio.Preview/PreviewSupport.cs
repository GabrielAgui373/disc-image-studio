using System.Globalization;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.Core;

namespace DiscImageStudio.Preview;

/// <summary>Validation and formatting shared by projection and artifact previews.</summary>
internal static class PreviewSupport
{
    public static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);

    public static string Flag(bool value) => value ? "true" : "false";

    /// <summary>Checks what every preview needs: right family and mode, sizes and sampling in range.</summary>
    public static void Validate(PreviewRequest request, OpticalDiscFamily family, PreviewMode mode)
    {
        if (request.Family != family || request.Mode != mode)
        {
            throw new PreviewFailedException(
                PreviewFailureCode.Unsupported,
                $"This service renders {family} {mode}, not {request.Family} {request.Mode}.");
        }

        if (request.ReadGeometry.Family != family)
        {
            throw new PreviewFailedException(
                PreviewFailureCode.InvalidParameters,
                "The measured geometry belongs to another disc family.");
        }

        if (request.OutputSize is < PreviewDefaults.MinimumSize or > PreviewDefaults.MaximumSize)
        {
            throw new PreviewFailedException(
                PreviewFailureCode.InvalidParameters,
                $"Preview size must be {PreviewDefaults.MinimumSize}..{PreviewDefaults.MaximumSize} pixels.");
        }

        int samples = request.EffectiveSamplesPerSector;
        if (samples < 1 || samples > PreviewDefaults.MaximumSamplesPerSector(family))
        {
            throw new PreviewFailedException(
                PreviewFailureCode.InvalidParameters,
                $"Samples per sector must be 1..{PreviewDefaults.MaximumSamplesPerSector(family)}.");
        }
    }

    public static IEnumerable<PreviewParameter> ToneParameters(ToneProfile tone, string prefix = "tone")
    {
        yield return new PreviewParameter($"{prefix}.ditherStrength", Invariant(tone.DitherStrength));
        yield return new PreviewParameter($"{prefix}.levels", Invariant(tone.Levels));
        yield return new PreviewParameter($"{prefix}.brightness", Invariant(tone.Brightness));
        yield return new PreviewParameter($"{prefix}.srgbLinearization", Flag(tone.SrgbLinearization));
    }

    /// <summary>Radii compared with a tolerance far below anything a user types or a sidecar rounds to.</summary>
    public static bool SameMm(double left, double right) => Math.Abs(left - right) < 1e-6;
}
