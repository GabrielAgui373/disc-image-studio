using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Preview;

/// <summary>
/// DVD read-back: the typed equivalent of the WPF <c>simulate</c> command. The ISO's
/// <see cref="DvdImageMetadata"/> sidecar gives sector count, geometry and drawing range;
/// payload bytes are descrambled and classified by <see cref="IsoReadbackRenderer.RenderPixels"/>
/// on a clockwise spiral with the scheme's 133.3 nm channel bit and PSN offset 0x30000.
/// </summary>
public sealed class DvdArtifactPreviewService : ArtifactPreviewService
{
    private const uint PsnOffset = 0x30000;
    private const int PayloadBytesPerSector = 2048;

    public override OpticalDiscFamily Family => OpticalDiscFamily.Dvd;

    protected override ArtifactOutput Render(PreviewRequest request, string artifactPath, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        DvdState project = (DvdState)workspace.Media;
        long isoSectors = new FileInfo(artifactPath).Length / PayloadBytesPerSector;
        DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(artifactPath);
        string state = metadata is null ? SidecarAbsent : SidecarFound;
        if (metadata is not null && (metadata.TotalSectors > isoSectors || metadata.DrawingEndLbaExclusive > isoSectors))
        {
            state = SidecarRejected;
            metadata = null;
        }

        uint totalSectors = metadata?.TotalSectors ?? checked((uint)Math.Min(isoSectors, project.Sectors));
        uint start = metadata?.DrawingStartLba ?? 0;
        uint end = metadata is { DrawingEndLbaExclusive: > 0 } ? metadata.DrawingEndLbaExclusive : totalSectors;
        DvdState recorded = metadata is null
            ? project
            : new DvdState(metadata.InnerRadiusMm, metadata.OuterRadiusMm, metadata.TotalSectors,
                metadata.PitchLinear, metadata.PitchQuadratic, metadata.PitchCubic);
        DvdState actual = request.MeasuredGeometry as DvdState ?? recorded;
        int samples = request.EffectiveSamplesPerSector;
        IsoReadbackOptions options = new(
            totalSectors,
            start,
            end - start,
            PsnOffset,
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            DvdStreamingOptions.StandardChannelBitLengthNm,
            Clockwise: true,
            request.OutputSize,
            samples,
            actual.PitchLinear,
            actual.PitchQuadratic,
            actual.PitchCubic);
        byte[] pixels = IsoReadbackRenderer.RenderPixels(artifactPath, options, out IsoReadbackSummary summary, cancellationToken);

        List<string> differences = [];
        IReadOnlyList<PreviewRingOverlay> fileSystem = [];
        List<PreviewParameter> parameters =
        [
            new("read.sectors", PreviewSupport.Invariant(totalSectors)),
            new("samplesPerSector", PreviewSupport.Invariant(samples)),
            new("drawing.startLba", PreviewSupport.Invariant(start)),
            new("drawing.endLbaExclusive", PreviewSupport.Invariant(end)),
            new("read.trackPitchUm", PreviewSupport.Invariant(summary.TrackPitchMicrometres)),
            new("classifiedSamples", PreviewSupport.Invariant(summary.ClassifiedSamples)),
            new("totalSamples", PreviewSupport.Invariant(summary.TotalSamples)),
            new("spiral", summary.SpiralDirection),
        ];
        if (metadata is not null)
        {
            CompareGeometry(differences, project, metadata.InnerRadiusMm, metadata.OuterRadiusMm, metadata.TotalSectors);
            if (!PreviewSupport.SameMm(metadata.PitchLinear, project.PitchLinear)
                || !PreviewSupport.SameMm(metadata.PitchQuadratic, project.PitchQuadratic)
                || !PreviewSupport.SameMm(metadata.PitchCubic, project.PitchCubic))
            {
                differences.Add("pitch");
            }

            if (metadata.HasFileSystem)
            {
                double length = totalSectors * 38688.0 * DvdStreamingOptions.StandardChannelBitLengthNm * 1e-6;
                DvdTrackGeometry geometry = DvdTrackGeometry.Create(
                    actual.InnerRadiusMm, actual.OuterRadiusMm, length,
                    actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic);
                fileSystem = PreviewOverlayPainter.FileSystemRings(
                    actual.InnerRadiusMm,
                    actual.OuterRadiusMm,
                    geometry.RadiusAtArcLength(length * ((double)start / totalSectors)),
                    geometry.RadiusAtArcLength(length * ((double)end / totalSectors)));
                parameters.Add(new("filesAtOuterRing", PreviewSupport.Flag(metadata.FilesAtOuterRing)));
                if (metadata.FilesAtOuterRing != workspace.Delivery.FilesAtOuterRing)
                {
                    differences.Add("filesAtOuterRing");
                }
            }
        }

        return new ArtifactOutput(
            pixels,
            new PreviewFrame((request.OutputSize - 1) / 2.0, actual.OuterRadiusMm * 2 / (request.OutputSize - 1)),
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            fileSystem,
            parameters,
            SidecarWarnings(state, artifactPath + ".json", differences),
            state);
    }
}
