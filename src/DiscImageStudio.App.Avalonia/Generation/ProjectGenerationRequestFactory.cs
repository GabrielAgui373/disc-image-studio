using DiscImageStudio.Application.Projects;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;
using DiscImageStudio.Generation;

namespace DiscImageStudio.App.Avalonia.Generation;

/// <summary>
/// Maps a validated <see cref="ProjectWorkspace"/> and a composed source image to the typed
/// generation request, with the same rules the WPF host applies to its form state. It lives in
/// the host because the request types belong to the disc engines, which the application layer
/// does not reference.
/// </summary>
public static class ProjectGenerationRequestFactory
{
    /// <param name="outputPath">
    /// The artifact path; for CD a <c>.wav</c> extension selects WAVE audio, as in the WPF host.
    /// </param>
    /// <param name="directBurn">CD only: raw little-endian audio for a direct burn.</param>
    public static GenerateDiscRequest Create(
        ProjectWorkspace workspace,
        string sourceImagePath,
        string? outputPath = null,
        bool directBurn = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        DeliveryState delivery = workspace.Delivery;
        string? dataDirectory = string.IsNullOrWhiteSpace(delivery.DataDirectory) ? null : delivery.DataDirectory.Trim();
        FilePlacement placement = delivery.FilesAtOuterRing ? FilePlacement.OuterRing : FilePlacement.InnerRing;
        string? label = string.IsNullOrWhiteSpace(delivery.VolumeLabel) ? null : delivery.VolumeLabel.Trim();

        return workspace.Media switch
        {
            DvdState dvd => new GenerateDvdRequest(
                sourceImagePath,
                new DvdStreamingOptions(
                    checked((uint)dvd.Sectors),
                    dvd.InnerRadiusMm,
                    dvd.OuterRadiusMm,
                    PitchLinear: dvd.PitchLinear,
                    PitchQuadratic: dvd.PitchQuadratic,
                    PitchCubic: dvd.PitchCubic,
                    Tone: workspace.Tone.Profile),
                dataDirectory,
                label ?? "DISC_IMAGE",
                placement),
            BluRayState bluRay => new GenerateBluRayRequest(
                sourceImagePath,
                new BluRayDiscParameters(checked((uint)bluRay.Sectors), 1, bluRay.InnerRadiusMm, bluRay.OuterRadiusMm),
                workspace.Tone.Profile,
                bluRay.InvertMapping,
                dataDirectory,
                label ?? "BD_IMAGE",
                placement),
            CdState cd => CreateCd(workspace, cd, sourceImagePath, outputPath ?? delivery.OutputPath, directBurn, dataDirectory, label, placement),
            _ => throw new ArgumentOutOfRangeException(nameof(workspace), "Unknown disc family."),
        };
    }

    private static GenerateCdRequest CreateCd(
        ProjectWorkspace workspace,
        CdState cd,
        string sourceImagePath,
        string? outputPath,
        bool directBurn,
        string? dataDirectory,
        string? label,
        FilePlacement placement)
    {
        CdArtifactFormat format = dataDirectory is not null
            ? CdArtifactFormat.Mode1Iso
            : directBurn
                ? CdArtifactFormat.RawAudio
                : string.Equals(Path.GetExtension(outputPath), ".wav", StringComparison.OrdinalIgnoreCase)
                    ? CdArtifactFormat.WaveAudio
                    : CdArtifactFormat.RawAudio;
        return new GenerateCdRequest(
            sourceImagePath,
            new CdDiscParameters(cd.InnerRadiusMm, cd.OuterRadiusMm, cd.Sectors),
            workspace.Tone.Profile,
            format,
            cd.Interleave,
            cd.WriteCue,
            directBurn || format == CdArtifactFormat.WaveAudio ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
            dataDirectory,
            label ?? "CD_IMAGE",
            placement);
    }
}
