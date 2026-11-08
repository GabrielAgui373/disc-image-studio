using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;

namespace DiscImageStudio.Preview;

/// <summary>
/// CD read-back, as the WPF read-back simulation. A <c>.iso</c> is a Mode 1 data disc read by
/// <see cref="CdDataImageGenerator.RenderTrackPixels"/>, whose sidecar gives geometry and drawing
/// range; any other file is an audio track (raw big-endian or WAVE) read by
/// <see cref="CdTrackGenerator.RenderTrackPixels"/>, whose sidecar gives geometry and whether the
/// delay interleave must be undone.
/// </summary>
public sealed class CdArtifactPreviewService : ArtifactPreviewService
{
    public override OpticalDiscFamily Family => OpticalDiscFamily.CompactDisc;

    protected override ArtifactOutput Render(PreviewRequest request, string artifactPath, CancellationToken cancellationToken)
        => Path.GetExtension(artifactPath).Equals(".iso", StringComparison.OrdinalIgnoreCase)
            ? RenderData(request, artifactPath, cancellationToken)
            : RenderAudio(request, artifactPath, cancellationToken);

    private static ArtifactOutput RenderAudio(PreviewRequest request, string path, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        CdState project = (CdState)workspace.Media;
        CdTrackMetadata? metadata = CdTrackMetadata.TryLoad(path);
        bool wave = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);
        string state = metadata is null ? SidecarAbsent : SidecarFound;

        // A raw track holds exactly the recorded sectors; a sidecar claiming another count
        // describes some other file. WAVE carries a header, so its length is not compared.
        if (metadata is not null && !wave && new FileInfo(path).Length != metadata.Sectors * CdDiscParameters.BytesPerSector)
        {
            state = SidecarRejected;
            metadata = null;
        }

        CdDiscParameters recorded = metadata is null
            ? new CdDiscParameters(project.InnerRadiusMm, project.OuterRadiusMm, project.Sectors)
            : new CdDiscParameters(
                metadata.InnerRadiusMm,
                metadata.OuterRadiusMm,
                metadata.Sectors,
                metadata.LinearVelocityMmPerSecond,
                metadata.StartAngleDegrees * Math.PI / 180.0,
                metadata.ImageOuterRadiusMm);
        bool deinterleave = metadata?.Interleaved ?? project.Interleave;
        CdDiscParameters actual = ReadGeometry(request, recorded);
        int samples = request.EffectiveSamplesPerSector;
        byte[] pixels = CdTrackGenerator.RenderTrackPixels(
            path,
            actual,
            request.OutputSize,
            Math.Max(1, CdDiscParameters.BytesPerSector / samples),
            deinterleave,
            cancellationToken);

        List<string> differences = [];
        if (metadata is not null)
        {
            CompareGeometry(differences, project, metadata.InnerRadiusMm, metadata.OuterRadiusMm, metadata.Sectors);
            if (metadata.Interleaved != project.Interleave)
            {
                differences.Add("interleave");
            }

            CompareTone(
                differences,
                workspace.Tone.Profile,
                new ToneProfile(metadata.ToneDitherStrength, metadata.ToneLevels, metadata.ToneBrightness, metadata.ToneSrgbLinearization));
        }

        return new ArtifactOutput(
            pixels,
            Frame(actual, request.OutputSize),
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            [],
            [
                new("cd.format", wave ? "wave" : "raw"),
                new("read.sectors", PreviewSupport.Invariant(actual.Sectors)),
                new("samplesPerSector", PreviewSupport.Invariant(samples)),
                new("deinterleave", PreviewSupport.Flag(deinterleave)),
            ],
            SidecarWarnings(state, CdTrackMetadata.PathFor(path), differences),
            state);
    }

    private static ArtifactOutput RenderData(PreviewRequest request, string path, CancellationToken cancellationToken)
    {
        ProjectWorkspace workspace = request.Workspace;
        CdState project = (CdState)workspace.Media;
        long length = new FileInfo(path).Length;
        CdDataImageMetadata? metadata = CdDataImageMetadata.TryLoad(path);
        string state = metadata is null ? SidecarAbsent : SidecarFound;
        if (metadata is not null && length != metadata.Parameters.Sectors * CdDataImageGenerator.BytesPerSector)
        {
            state = SidecarRejected;
            metadata = null;
        }

        // Without a usable sidecar the ISO's own length gives the sector count; the engine reads
        // the whole image because no drawing range is known.
        CdDiscParameters recorded = metadata?.Parameters
            ?? new CdDiscParameters(project.InnerRadiusMm, project.OuterRadiusMm, length / CdDataImageGenerator.BytesPerSector);
        CdDiscParameters actual = ReadGeometry(request, recorded);
        int samples = request.EffectiveSamplesPerSector;
        byte[] pixels = CdDataImageGenerator.RenderTrackPixels(
            path,
            actual,
            request.OutputSize,
            Math.Max(1, CdDataImageGenerator.BytesPerSector / samples),
            cancellationToken);

        List<string> differences = [];
        IReadOnlyList<PreviewRingOverlay> fileSystem = [];
        List<PreviewParameter> parameters =
        [
            new("cd.format", "data"),
            new("read.sectors", PreviewSupport.Invariant(actual.Sectors)),
            new("samplesPerSector", PreviewSupport.Invariant(samples)),
        ];
        if (metadata is not null)
        {
            CompareGeometry(differences, project, metadata.Parameters.InnerRadiusMm, metadata.Parameters.OuterRadiusMm, metadata.Parameters.Sectors);
            CompareTone(differences, workspace.Tone.Profile, metadata.Tone);
            if (metadata.FilesAtOuterRing != workspace.Delivery.FilesAtOuterRing)
            {
                differences.Add("filesAtOuterRing");
            }

            double drawingInner = actual.RadiusFromByte((long)metadata.DrawingStartLba * CdDiscParameters.BytesPerSector);
            double drawingOuter = actual.RadiusFromByte((long)metadata.DrawingEndLbaExclusive * CdDiscParameters.BytesPerSector);
            fileSystem = PreviewOverlayPainter.FileSystemRings(actual.InnerRadiusMm, actual.OuterRadiusMm, drawingInner, drawingOuter);
            parameters.Add(new("drawing.startLba", PreviewSupport.Invariant(metadata.DrawingStartLba)));
            parameters.Add(new("drawing.endLbaExclusive", PreviewSupport.Invariant(metadata.DrawingEndLbaExclusive)));
            parameters.Add(new("filesAtOuterRing", PreviewSupport.Flag(metadata.FilesAtOuterRing)));
        }

        return new ArtifactOutput(
            pixels,
            Frame(actual, request.OutputSize),
            actual.InnerRadiusMm,
            actual.OuterRadiusMm,
            fileSystem,
            parameters,
            SidecarWarnings(state, path + ".json", differences),
            state);
    }

    /// <summary>The recorded geometry, with the measured radii when the request carries them.</summary>
    private static CdDiscParameters ReadGeometry(PreviewRequest request, CdDiscParameters recorded)
    {
        CdDiscParameters actual = request.MeasuredGeometry is { } measured
            ? recorded with { InnerRadiusMm = measured.InnerRadiusMm, OuterRadiusMm = measured.OuterRadiusMm }
            : recorded;
        actual.Validate();
        return actual;
    }

    private static PreviewFrame Frame(CdDiscParameters actual, int size)
        => new(size / 2.0, actual.ImageOuterRadiusMm * 2 / size);
}
