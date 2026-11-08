using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DvdImageSolver.Solver;

namespace DiscImageStudio.Preview;

/// <summary>
/// DVD projection: the typed equivalent of the WPF <c>calibrate</c> command, calling
/// <see cref="CalibrationPreviewRenderer.RenderPixels"/> directly with the same fixed settings
/// (clockwise spiral, 133.3 nm channel bit, thresholds 128/1, two tone levels). With a data folder
/// the hybrid layout is planned first and only its drawing range is projected.
/// </summary>
public sealed class DvdProjectionPreviewService(PreviewSourceCache sources) : ProjectionPreviewService(sources)
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.Dvd;

    protected override ProjectionOutput Render(PreviewRequest request, string sourcePath, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        DvdState generated = Dvd(workspace.Media);
        DvdState actual = Dvd(request.ReadGeometry);
        ToneProfile tone = workspace.Tone.Profile with { Levels = ToneProfile.TwoLevels };
        uint totalSectors = checked((uint)generated.Sectors);
        if (actual.Sectors != generated.Sectors)
        {
            throw new ArgumentException("The measured DVD geometry must keep the generated sector count.");
        }

        uint drawingStart = 0;
        uint drawingSectors = totalSectors;
        DvdHybridStreamingPlan? plan = null;
        string? dataDirectory = DataDirectory(workspace);
        if (dataDirectory is not null)
        {
            DvdStreamingOptions options = Options(generated);
            plan = PrepareLayout(() => DvdStreamingGenerator.PrepareHybrid(
                dataDirectory,
                options,
                VolumeLabel(workspace, "DISC_IMAGE"),
                workspace.Delivery.FilesAtOuterRing));
            drawingStart = plan.DrawingStartLba;
            drawingSectors = plan.DrawingSectors;
            cancellationToken.ThrowIfCancellationRequested();
        }

        int size = request.OutputSize;
        int samples = request.EffectiveSamplesPerSector;
        CalibrationPreviewOptions calibration = new(
            totalSectors,
            drawingStart,
            drawingSectors,
            generated.InnerRadiusMm,
            generated.OuterRadiusMm,
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            DvdStreamingOptions.StandardChannelBitLengthNm,
            Clockwise: true,
            LuminanceThreshold: 128,
            AlphaThreshold: 1,
            size,
            samples,
            generated.PitchLinear,
            generated.PitchQuadratic,
            generated.PitchCubic,
            actual.PitchLinear,
            actual.PitchQuadratic,
            actual.PitchCubic,
            tone);
        byte[] pixels = CalibrationPreviewRenderer.RenderPixels(
            sourcePath, calibration, out CalibrationPreviewSummary summary, cancellationToken);

        List<PreviewParameter> parameters =
        [
            new("samplesPerSector", Invariant(samples)),
            new("drawing.startLba", Invariant(drawingStart)),
            new("drawing.endLbaExclusive", Invariant(drawingStart + drawingSectors)),
            new("generated.trackPitchUm", Invariant(summary.GeneratedTrackPitchMicrometres)),
            new("read.trackPitchUm", Invariant(summary.ActualTrackPitchMicrometres)),
            new("spiral", summary.SpiralDirection),
        ];
        IReadOnlyList<PreviewRingOverlay> fileSystem = [];
        if (plan is not null)
        {
            parameters.Add(new("filesAtOuterRing", plan.FilesAtOuterRing ? "true" : "false"));

            // Same mapping as the WPF overlay: the LBA fraction of the track length, on the
            // measured spiral.
            double length = totalSectors * 38688.0 * DvdStreamingOptions.StandardChannelBitLengthNm * 1e-6;
            DvdTrackGeometry geometry = DvdTrackGeometry.Create(
                actual.InnerRadiusMm, actual.OuterRadiusMm, length,
                actual.PitchLinear, actual.PitchQuadratic, actual.PitchCubic);
            double drawingInner = geometry.RadiusAtArcLength(length * ((double)plan.DrawingStartLba / totalSectors));
            double drawingOuter = geometry.RadiusAtArcLength(length * ((double)plan.DrawingEndLbaExclusive / totalSectors));
            fileSystem = PreviewOverlayPainter.FileSystemRings(
                actual.InnerRadiusMm, actual.OuterRadiusMm, drawingInner, drawingOuter);
        }

        PreviewFrame frame = new((size - 1) / 2.0, actual.OuterRadiusMm * 2 / (size - 1));
        return new ProjectionOutput(pixels, frame, fileSystem, parameters);
    }

    private static DvdState Dvd(MediaState media)
        => media as DvdState ?? throw new ArgumentException("A DVD preview needs DVD geometry.");

    private static DvdStreamingOptions Options(DvdState dvd)
    {
        DvdStreamingOptions options = new(
            checked((uint)dvd.Sectors),
            dvd.InnerRadiusMm,
            dvd.OuterRadiusMm,
            PitchLinear: dvd.PitchLinear,
            PitchQuadratic: dvd.PitchQuadratic,
            PitchCubic: dvd.PitchCubic);
        options.Validate();
        return options;
    }
}
