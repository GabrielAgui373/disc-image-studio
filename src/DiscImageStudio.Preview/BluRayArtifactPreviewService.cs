using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.Preview.BluRay;

namespace DiscImageStudio.Preview;

/// <summary>
/// Blu-ray read-back: the ISO's <see cref="BluRayIsoMetadata"/> sidecar restores geometry and
/// drawing range, and <see cref="BluRayPreviewRenderer.RenderReadback"/> classifies the
/// descrambled payload into the nominal 3T/6T map. Without a sidecar the project geometry and
/// the whole disc are used, as the WPF host did.
/// </summary>
public sealed class BluRayArtifactPreviewService : ArtifactPreviewService
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.BluRay;

    protected override ArtifactOutput Render(PreviewRequest request, string artifactPath, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        BluRayState project = (BluRayState)workspace.Media;
        long length = new FileInfo(artifactPath).Length;
        BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(artifactPath);
        string state = metadata is null ? SidecarAbsent : SidecarFound;
        if (metadata is not null && metadata.ContentLength != length)
        {
            state = SidecarRejected;
            metadata = null;
        }

        uint sectors = metadata?.TotalSectors ?? checked((uint)project.Sectors);
        double inner = request.MeasuredGeometry?.InnerRadiusMm ?? metadata?.InnerRadiusMm ?? project.InnerRadiusMm;
        double outer = request.MeasuredGeometry?.OuterRadiusMm ?? metadata?.OuterRadiusMm ?? project.OuterRadiusMm;
        BluRayDiscParameters actual = new(sectors, 1, inner, outer);
        actual.Validate();
        uint start = metadata?.DrawingStartSector ?? 0;
        uint end = metadata?.DrawingEndSectorExclusive ?? sectors;
        byte[] pixels = BluRayPreviewRenderer.RenderReadback(
            artifactPath, actual, request.OutputSize, start, end, cancellationToken);

        List<string> differences = [];
        IReadOnlyList<PreviewRingOverlay> fileSystem = [];
        List<PreviewParameter> parameters =
        [
            new("read.sectors", PreviewSupport.Invariant(sectors)),
            new("drawing.startSector", PreviewSupport.Invariant(start)),
            new("drawing.endSectorExclusive", PreviewSupport.Invariant(end)),
        ];
        if (metadata is not null)
        {
            CompareGeometry(differences, project, metadata.InnerRadiusMm, metadata.OuterRadiusMm, metadata.TotalSectors);
            CompareTone(differences, workspace.Tone.Profile with { Levels = ToneProfile.TwoLevels }, metadata.Tone);
            if (metadata.InvertMapping != project.InvertMapping)
            {
                differences.Add("invertMapping");
            }

            parameters.Add(new("invertMapping", PreviewSupport.Flag(metadata.InvertMapping)));
            if (metadata.DataDirectory is not null)
            {
                BluRayTrackGeometry geometry = new(actual);
                fileSystem = PreviewOverlayPainter.FileSystemRings(
                    actual.InnerRadiusMm,
                    actual.OuterRadiusMm,
                    geometry.AtSector(start).RadiusMm,
                    geometry.AtSector(end).RadiusMm);
                parameters.Add(new("filesAtOuterRing", PreviewSupport.Flag(metadata.FilesAtOuterRing)));
            }
        }

        (double center, double mmPerPixel) = BluRayPreviewRenderer.Frame(actual, request.OutputSize);
        return new ArtifactOutput(
            pixels,
            new PreviewFrame(center, mmPerPixel),
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            fileSystem,
            parameters,
            SidecarWarnings(state, BluRayIsoMetadata.PathFor(artifactPath), differences),
            state);
    }
}
