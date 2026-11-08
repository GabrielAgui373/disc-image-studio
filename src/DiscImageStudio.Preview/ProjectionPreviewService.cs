using System.Diagnostics;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.Rendering;

namespace DiscImageStudio.Preview;

/// <summary>
/// Shared flow of the three projection previews: validate the request, lease the project's
/// composed 8192 px generation source (the exact input generation reads), run the family's
/// engine off the calling thread and describe the result. Subclasses only map the project to
/// engine parameters and call the engine.
/// </summary>
public abstract class ProjectionPreviewService : IPreviewService
{
    private readonly PreviewSourceCache _sources;

    protected ProjectionPreviewService(PreviewSourceCache sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public abstract OpticalDiscFamily Family { get; }

    public PreviewMode Mode => PreviewMode.Projection;

    public async Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreviewSupport.Validate(request, Family, PreviewMode.Projection);
        if (SourceSize(request) is < PreviewDefaults.MinimumSourceSize or > PreviewDefaults.MaximumSize)
        {
            throw new PreviewFailedException(
                PreviewFailureCode.InvalidParameters,
                $"Source size must be {PreviewDefaults.MinimumSourceSize}..{PreviewDefaults.MaximumSize} pixels.");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        using PreviewSourceLease source = await _sources
            .AcquireAsync(request.Workspace, request.ProjectFilePath, SourceSize(request), cancellationToken)
            .ConfigureAwait(false);
        if (source.PaintedLayerCount == 0)
        {
            throw new PreviewFailedException(
                PreviewFailureCode.NothingToProject,
                "No visible layer could be painted, so there is nothing to project.");
        }

        ProjectionOutput output;
        try
        {
            output = await Task.Run(() => Render(request, source.Path, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new PreviewFailedException(PreviewFailureCode.InvalidParameters, exception.Message, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<PreviewWarning> warnings = [];
        if (source.Issues.Count > 0)
        {
            warnings.Add(new PreviewWarning(
                PreviewWarningCode.LayersNotPainted,
                string.Join(", ", source.Issues.Select(issue => issue.LayerId))));
        }

        if (source.Size < ProjectCompositionSize)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.DraftSource, Invariant(source.Size)));
        }

        if (request.MeasuredGeometry is null)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.MeasuredGeometryAssumed));
        }

        List<PreviewParameter> parameters =
        [
            new("family", Family.ToString()),
            new("mode", Mode.ToString()),
            new("size", Invariant(request.OutputSize)),
            new("sourceSize", Invariant(source.Size)),
            new("paintedLayers", Invariant(source.PaintedLayerCount)),
            new("generated.innerRadiusMm", Invariant(request.Workspace.Media.InnerRadiusMm)),
            new("generated.outerRadiusMm", Invariant(request.Workspace.Media.OuterRadiusMm)),
            new("generated.sectors", Invariant(request.Workspace.Media.Sectors)),
            new("read.innerRadiusMm", Invariant(request.ReadGeometry.InnerRadiusMm)),
            new("read.outerRadiusMm", Invariant(request.ReadGeometry.OuterRadiusMm)),
            new("read.sectors", Invariant(request.ReadGeometry.Sectors)),
            .. PreviewSupport.ToneParameters(request.Workspace.Tone.Profile),
            .. output.Parameters,
        ];
        List<PreviewRingOverlay> overlays =
        [
            new(PreviewOverlayKind.UsefulRing, request.ReadGeometry.InnerRadiusMm, request.ReadGeometry.OuterRadiusMm),
            .. output.FileSystem,
        ];
        stopwatch.Stop();
        return new PreviewResult(
            Family,
            Mode,
            request.OutputSize,
            output.Pixels,
            output.Frame,
            PreviewConfidence.Exact,
            overlays,
            warnings,
            parameters,
            stopwatch.Elapsed,
            request.Revision);
    }

    /// <summary>Side of the composed source the engines read, in pixels.</summary>
    protected static int ProjectCompositionSize => Application.Composition.ProjectCompositionService.GenerationSize;

    /// <summary>Blocking engine call; runs on a worker thread.</summary>
    protected abstract ProjectionOutput Render(PreviewRequest request, string sourcePath, CancellationToken cancellationToken);

    private static int SourceSize(PreviewRequest request) => request.SourceSize ?? ProjectCompositionSize;

    protected static string Invariant(IFormattable value) => PreviewSupport.Invariant(value);

    /// <summary>The data folder generation would lay out, or <see langword="null"/> for a pure image disc.</summary>
    protected static string? DataDirectory(ProjectWorkspace workspace)
        => string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory) ? null : workspace.Delivery.DataDirectory.Trim();

    protected static string VolumeLabel(ProjectWorkspace workspace, string fallback)
        => string.IsNullOrWhiteSpace(workspace.Delivery.VolumeLabel) ? fallback : workspace.Delivery.VolumeLabel.Trim();

    /// <summary>Wraps data-layout failures (unreadable folder, files that do not fit) as a preview failure.</summary>
    protected static T PrepareLayout<T>(Func<T> prepare)
    {
        try
        {
            return prepare();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or InvalidOperationException)
        {
            throw new PreviewFailedException(PreviewFailureCode.DataLayoutFailed, exception.Message, exception);
        }
    }
}

/// <summary>What an engine produced, plus the family-specific facts the result reports.</summary>
public sealed record ProjectionOutput(
    byte[] Pixels,
    PreviewFrame Frame,
    IReadOnlyList<PreviewRingOverlay> FileSystem,
    IReadOnlyList<PreviewParameter> Parameters);
