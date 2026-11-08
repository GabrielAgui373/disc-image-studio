using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.BluRay;
using DiscImageStudio.Preview.BluRay;
using DiscImageStudio.Services;

namespace DiscImageStudio.Preview;

/// <summary>
/// Blu-ray projection, as the WPF design projection: the drawing plan always follows the
/// project's current folder and placement (a finished ISO's sidecar never overrides later edits),
/// the source is loaded with the tone applied at source resolution, and the nominal 3T/6T class
/// map is drawn through the measured geometry.
/// </summary>
public sealed class BluRayProjectionPreviewService(PreviewSourceCache sources) : ProjectionPreviewService(sources)
{
    public override DiscImageStudio.Core.OpticalDiscFamily Family => DiscImageStudio.Core.OpticalDiscFamily.BluRay;

    protected override ProjectionOutput Render(PreviewRequest request, string sourcePath, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        BluRayState state = workspace.Media as BluRayState
            ?? throw new ArgumentException("A Blu-ray preview needs Blu-ray geometry.");
        BluRayDiscParameters generated = Parameters(state);
        BluRayDiscParameters actual = Parameters(request.ReadGeometry);
        string? dataDirectory = DataDirectory(workspace);
        BluRayIsoPlan plan = PrepareLayout(() => BluRayIsoGenerator.PrepareDrawing(
            generated,
            dataDirectory,
            VolumeLabel(workspace, "BD_IMAGE"),
            filesAtOuterRing: dataDirectory is not null && workspace.Delivery.FilesAtOuterRing));
        cancellationToken.ThrowIfCancellationRequested();

        BluRayGray8Raster source = BluRayImageLoader.Load(sourcePath, workspace.Tone.Profile, cancellationToken);
        int size = request.OutputSize;
        byte[] pixels = BluRayPreviewRenderer.RenderCalibration(
            source,
            generated,
            actual,
            plan.DrawingStartSector,
            plan.DrawingEndSectorExclusive,
            size,
            state.InvertMapping,
            cancellationToken);

        IReadOnlyList<PreviewRingOverlay> fileSystem = [];
        if (plan.HasFileSystem)
        {
            BluRayTrackGeometry geometry = new(actual);
            fileSystem = PreviewOverlayPainter.FileSystemRings(
                actual.InnerRadiusMm,
                actual.OuterRadiusMm,
                geometry.AtSector(plan.DrawingStartSector).RadiusMm,
                geometry.AtSector(plan.DrawingEndSectorExclusive).RadiusMm);
        }

        (double center, double mmPerPixel) = BluRayPreviewRenderer.Frame(actual, size);
        return new ProjectionOutput(
            pixels,
            new PreviewFrame(center, mmPerPixel),
            fileSystem,
            [
                new("invertMapping", state.InvertMapping ? "true" : "false"),
                new("drawing.startSector", Invariant(plan.DrawingStartSector)),
                new("drawing.endSectorExclusive", Invariant(plan.DrawingEndSectorExclusive)),
                new("filesAtOuterRing", plan.FilesAtOuterRing ? "true" : "false"),
            ]);
    }

    private static BluRayDiscParameters Parameters(MediaState media)
    {
        BluRayDiscParameters disc = new(checked((uint)media.Sectors), 1, media.InnerRadiusMm, media.OuterRadiusMm);
        disc.Validate();
        return disc;
    }
}
