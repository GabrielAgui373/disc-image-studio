using System.Diagnostics;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.Preview;

/// <summary>
/// Shared flow of the three artifact read-backs: find the project's generated file, let the
/// family read it with the geometry its sidecar records (or the project's, with a confidence
/// warning when the sidecar is missing or does not describe the file) and describe the result.
/// The artifact is only read; nothing next to it is written.
/// </summary>
public abstract class ArtifactPreviewService : IPreviewService
{
    public abstract OpticalDiscFamily Family { get; }

    public PreviewMode Mode => PreviewMode.Artifact;

    /// <summary>
    /// The artifact generation writes for <paramref name="workspace"/>: the delivery output path,
    /// resolved against the project folder when stored relative. <see langword="null"/> when unset.
    /// </summary>
    public static string? ArtifactPath(ProjectWorkspace workspace, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string stored = workspace.Delivery.OutputPath;
        return string.IsNullOrWhiteSpace(stored)
            ? null
            : ProjectPaths.ResolveAsset(projectFilePath, stored.Trim()) ?? stored.Trim();
    }

    public async Task<PreviewResult> RenderAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreviewSupport.Validate(request, Family, PreviewMode.Artifact);
        string? path = ArtifactPath(request.Workspace, request.ProjectFilePath);
        if (path is null || !File.Exists(path))
        {
            throw new PreviewFailedException(
                PreviewFailureCode.ArtifactMissing,
                path is null ? "The project has no output path." : $"No generated artifact at '{path}'.");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        FileInfo file = new(path);
        ArtifactOutput output;
        try
        {
            output = await Task.Run(() => Render(request, file.FullName, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException
                                              or UnauthorizedAccessException or OverflowException)
        {
            throw new PreviewFailedException(PreviewFailureCode.ArtifactUnreadable, exception.Message, exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<PreviewWarning> warnings = [.. output.Warnings];
        if (request.MeasuredGeometry is null)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.MeasuredGeometryAssumed));
        }

        bool approximate = warnings.Any(warning => warning.Code is PreviewWarningCode.SidecarMissing
                                                                  or PreviewWarningCode.SidecarMismatch);
        List<PreviewParameter> parameters =
        [
            new("family", Family.ToString()),
            new("mode", Mode.ToString()),
            new("size", PreviewSupport.Invariant(request.OutputSize)),
            new("artifact.path", file.FullName),
            new("artifact.bytes", PreviewSupport.Invariant(file.Length)),
            new("artifact.modifiedUtc", file.LastWriteTimeUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            new("sidecar", output.SidecarState),
            new("read.innerRadiusMm", PreviewSupport.Invariant(output.ReadInnerRadiusMm)),
            new("read.outerRadiusMm", PreviewSupport.Invariant(output.ReadOuterRadiusMm)),
            .. output.Parameters,
        ];
        List<PreviewRingOverlay> overlays =
        [
            new(PreviewOverlayKind.UsefulRing, output.ReadInnerRadiusMm, output.ReadOuterRadiusMm),
            .. output.FileSystem,
        ];
        stopwatch.Stop();
        return new PreviewResult(
            Family,
            Mode,
            request.OutputSize,
            output.Pixels,
            output.Frame,
            approximate ? PreviewConfidence.Approximate : PreviewConfidence.Exact,
            overlays,
            warnings,
            parameters,
            stopwatch.Elapsed,
            request.Revision);
    }

    /// <summary>Blocking read of <paramref name="artifactPath"/>; runs on a worker thread.</summary>
    protected abstract ArtifactOutput Render(PreviewRequest request, string artifactPath, CancellationToken cancellationToken);

    /// <summary>Sidecar states reported as the <c>sidecar</c> parameter.</summary>
    protected const string SidecarFound = "found";
    protected const string SidecarAbsent = "missing";
    protected const string SidecarRejected = "mismatch";

    /// <summary>The warnings for a sidecar outcome, plus <see cref="PreviewWarningCode.ArtifactDiffersFromProject"/>.</summary>
    protected static List<PreviewWarning> SidecarWarnings(string state, string sidecarPath, IReadOnlyList<string> differences)
    {
        List<PreviewWarning> warnings = [];
        if (state == SidecarAbsent)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.SidecarMissing, sidecarPath));
        }
        else if (state == SidecarRejected)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.SidecarMismatch, sidecarPath));
        }

        if (differences.Count > 0)
        {
            warnings.Add(new PreviewWarning(PreviewWarningCode.ArtifactDiffersFromProject, string.Join(", ", differences)));
        }

        return warnings;
    }

    /// <summary>Names of the geometry fields where the artifact and the project disagree.</summary>
    protected static void CompareGeometry(List<string> differences, MediaState project, double inner, double outer, long sectors)
    {
        if (!PreviewSupport.SameMm(project.InnerRadiusMm, inner))
        {
            differences.Add("innerRadiusMm");
        }

        if (!PreviewSupport.SameMm(project.OuterRadiusMm, outer))
        {
            differences.Add("outerRadiusMm");
        }

        if (project.Sectors != sectors)
        {
            differences.Add("sectors");
        }
    }

    protected static void CompareTone(List<string> differences, ToneProfile project, ToneProfile artifact)
    {
        if (project.Normalized() != artifact.Normalized())
        {
            differences.Add("tone");
        }
    }
}

/// <summary>What a family read from its artifact, plus the facts the result reports.</summary>
public sealed record ArtifactOutput(
    byte[] Pixels,
    PreviewFrame Frame,
    double ReadInnerRadiusMm,
    double ReadOuterRadiusMm,
    IReadOnlyList<PreviewRingOverlay> FileSystem,
    IReadOnlyList<PreviewParameter> Parameters,
    IReadOnlyList<PreviewWarning> Warnings,
    string SidecarState);
