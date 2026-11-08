using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Preview;

/// <summary>
/// What a preview shows. <see cref="Projection"/> runs the current design through the engine's
/// geometry without writing an artifact (the WPF "calibration" preview); <see cref="Artifact"/>
/// reads a generated file back (the WPF "read-back simulation").
/// </summary>
public enum PreviewMode
{
    Projection,
    Artifact,
}

/// <summary>
/// How far the picture can be trusted. <see cref="Exact"/> means every input the engine needs was
/// available; <see cref="Approximate"/> means a fallback filled a gap (for example a missing
/// sidecar), which <see cref="PreviewResult.Warnings"/> explains.
/// </summary>
public enum PreviewConfidence
{
    Exact,
    Approximate,
}

/// <summary>Stable codes for conditions that did not stop the preview. Hosts localize them.</summary>
public enum PreviewWarningCode
{
    /// <summary>Some visible layers could not be painted (missing, unreadable or too large).</summary>
    LayersNotPainted,

    /// <summary>No measured geometry was given; the projection reads with the generated one.</summary>
    MeasuredGeometryAssumed,

    /// <summary>The projection read a source smaller than the generation source (a draft).</summary>
    DraftSource,

    /// <summary>The artifact has no sidecar; geometry and drawing range come from the project.</summary>
    SidecarMissing,

    /// <summary>The sidecar does not describe this artifact (sector count or size differs).</summary>
    SidecarMismatch,

    /// <summary>The artifact was written before the project's last change.</summary>
    ArtifactOlderThanProject,
}

/// <summary>Stable codes for conditions that prevent a preview. Hosts localize them.</summary>
public enum PreviewFailureCode
{
    /// <summary>The request asks for a family or mode no registered service renders.</summary>
    Unsupported,

    /// <summary>No visible layer could be painted, so there is nothing to project.</summary>
    NothingToProject,

    /// <summary>The project's geometry or the requested parameters are out of range.</summary>
    InvalidParameters,

    /// <summary>The data folder could not be laid out on the disc.</summary>
    DataLayoutFailed,

    /// <summary>No generated artifact was found for the project.</summary>
    ArtifactMissing,

    /// <summary>The artifact exists but cannot be read as this family's image.</summary>
    ArtifactUnreadable,
}

public sealed record PreviewWarning(PreviewWarningCode Code, string? Detail = null);

/// <summary>A preview that could not be produced; <see cref="Exception.Message"/> is diagnostic only.</summary>
public sealed class PreviewFailedException(PreviewFailureCode code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public PreviewFailureCode Code { get; } = code;
}

/// <summary>
/// One preview to render. <paramref name="Workspace"/> is the immutable snapshot the preview
/// describes, so later edits never leak into a running calculation.
/// </summary>
/// <param name="MeasuredGeometry">
/// Geometry the disc is read with. <see langword="null"/> reads with the project's own geometry,
/// which shows the design exactly as generated.
/// </param>
/// <param name="SamplesPerSector">
/// Track sampling density; <see langword="null"/> uses the family default
/// (<see cref="PreviewDefaults.SamplesPerSector"/>). Blu-ray ignores it.
/// </param>
/// <param name="Revision">Caller's revision; echoed in the result so stale results can be dropped.</param>
/// <param name="SourceSize">
/// Projection only: side of the composed source. <see langword="null"/> uses the generation size
/// (8192 px), so the projection reads exactly what generation would; smaller values are a draft
/// that saves memory and reports <see cref="PreviewWarningCode.DraftSource"/>.
/// </param>
public sealed record PreviewRequest(
    ProjectWorkspace Workspace,
    string? ProjectFilePath,
    PreviewMode Mode,
    int OutputSize,
    MediaState? MeasuredGeometry = null,
    int? SamplesPerSector = null,
    long Revision = 0,
    int? SourceSize = null)
{
    public OpticalDiscFamily Family => Workspace.Media.Family;

    public MediaState ReadGeometry => MeasuredGeometry ?? Workspace.Media;

    public int EffectiveSamplesPerSector => SamplesPerSector ?? PreviewDefaults.SamplesPerSector(Family);
}

/// <summary>
/// Maps pixel coordinates to disc millimetres, because each engine centres its raster
/// differently: <c>mm = (pixel + PixelOffset - CenterPx) * MmPerPixel</c> on both axes.
/// </summary>
public sealed record PreviewFrame(double CenterPx, double MmPerPixel, double PixelOffset = 0)
{
    public double RadiusMmAt(double x, double y)
    {
        double dx = (x + PixelOffset - CenterPx) * MmPerPixel;
        double dy = (y + PixelOffset - CenterPx) * MmPerPixel;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

public enum PreviewOverlayKind
{
    /// <summary>The recorded track: from the inner to the outer radius of the read geometry.</summary>
    UsefulRing,

    /// <summary>Space taken by the filesystem: inside the track but outside the drawing range.</summary>
    FileSystem,
}

/// <summary>An annulus on the disc, in millimetres. Drawn by the host or burned in for export.</summary>
public sealed record PreviewRingOverlay(PreviewOverlayKind Kind, double InnerRadiusMm, double OuterRadiusMm);

/// <summary>A parameter the engine actually used, as invariant text for display and diagnostics.</summary>
public sealed record PreviewParameter(string Key, string Value);

/// <summary>
/// A rendered preview. <see cref="Pixels"/> is an opaque BGRA buffer of
/// <see cref="Size"/>² that the host displays and exports unchanged, so the exported PNG is
/// exactly what is on screen.
/// </summary>
public sealed record PreviewResult(
    OpticalDiscFamily Family,
    PreviewMode Mode,
    int Size,
    byte[] Pixels,
    PreviewFrame Frame,
    PreviewConfidence Confidence,
    IReadOnlyList<PreviewRingOverlay> Overlays,
    IReadOnlyList<PreviewWarning> Warnings,
    IReadOnlyList<PreviewParameter> Parameters,
    TimeSpan Elapsed,
    long Revision);

/// <summary>Renders one family in one mode. Implementations are blocking-free and never touch UI types.</summary>
public interface IPreviewService
{
    OpticalDiscFamily Family { get; }

    PreviewMode Mode { get; }

    Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken cancellationToken);
}

/// <summary>Defaults and limits shared by every family, matching the WPF preview settings.</summary>
public static class PreviewDefaults
{
    public const int MinimumSize = 256;
    public const int MaximumSize = 8192;

    /// <summary>Smallest draft source a projection accepts.</summary>
    public const int MinimumSourceSize = 1024;

    public static int OutputSize(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.CompactDisc => 1600,
        OpticalDiscFamily.Dvd => 1200,
        _ => 2048,
    };

    public static int SamplesPerSector(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.CompactDisc => 16,
        OpticalDiscFamily.Dvd => 4,
        _ => 1,
    };

    /// <summary>Upper bound of <see cref="PreviewRequest.SamplesPerSector"/> per family.</summary>
    public static int MaximumSamplesPerSector(OpticalDiscFamily family) => family switch
    {
        OpticalDiscFamily.CompactDisc => 2352,
        OpticalDiscFamily.Dvd => 16,
        _ => 1,
    };
}
