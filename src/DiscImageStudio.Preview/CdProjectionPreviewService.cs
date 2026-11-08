using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

namespace DiscImageStudio.Preview;

/// <summary>
/// CD projection, as the WPF calibration preview: an audio disc runs
/// <see cref="CdTrackGenerator.RenderWarpPixels"/>; with a data folder the ISO layout is planned
/// first and <see cref="CdDataImageGenerator.RenderWarpPixels"/> draws only its drawing range,
/// with the filesystem space reported as an overlay.
/// </summary>
public sealed class CdProjectionPreviewService(PreviewSourceCache sources) : ProjectionPreviewService(sources)
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.CompactDisc;

    protected override ProjectionOutput Render(PreviewRequest request, string sourcePath, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        CdDiscParameters generated = Parameters(workspace.Media);
        CdDiscParameters actual = Parameters(request.ReadGeometry);
        ToneProfile tone = workspace.Tone.Profile;
        int size = request.OutputSize;
        int samples = request.EffectiveSamplesPerSector;
        PreviewFrame frame = new(size / 2.0, actual.ImageOuterRadiusMm * 2 / size);
        string? dataDirectory = DataDirectory(workspace);
        if (dataDirectory is null)
        {
            byte[] audio = CdTrackGenerator.RenderWarpPixels(
                sourcePath, generated, actual, size, samples, tone, cancellationToken);
            return new ProjectionOutput(
                audio,
                frame,
                [],
                [new("cd.format", "audio"), new("samplesPerSector", Invariant(samples))]);
        }

        CdDataLayoutPlan plan = PrepareLayout(() => CdDataImageGenerator.PrepareLayout(
            dataDirectory,
            generated,
            VolumeLabel(workspace, "CD_IMAGE"),
            workspace.Delivery.FilesAtOuterRing));
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = CdDataImageGenerator.RenderWarpPixels(
            sourcePath, generated, actual, size, samples, tone, plan, cancellationToken);

        // Same mapping as the WPF overlay: LBAs are placed on the measured geometry.
        double drawingInner = actual.RadiusFromByte((long)plan.DrawingStartLba * CdDiscParameters.BytesPerSector);
        double drawingOuter = actual.RadiusFromByte((long)plan.DrawingEndLbaExclusive * CdDiscParameters.BytesPerSector);
        return new ProjectionOutput(
            pixels,
            frame,
            PreviewOverlayPainter.FileSystemRings(actual.InnerRadiusMm, actual.OuterRadiusMm, drawingInner, drawingOuter),
            [
                new("cd.format", "data"),
                new("samplesPerSector", Invariant(samples)),
                new("drawing.startLba", Invariant(plan.DrawingStartLba)),
                new("drawing.endLbaExclusive", Invariant(plan.DrawingEndLbaExclusive)),
                new("filesAtOuterRing", workspace.Delivery.FilesAtOuterRing ? "true" : "false"),
            ]);
    }

    private static CdDiscParameters Parameters(MediaState media)
    {
        CdDiscParameters parameters = new(media.InnerRadiusMm, media.OuterRadiusMm, media.Sectors);
        parameters.Validate();
        return parameters;
    }
}
