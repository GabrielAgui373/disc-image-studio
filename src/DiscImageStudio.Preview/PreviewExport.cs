using System.Globalization;
using System.Text;
using DiscImageStudio.Application.Preview;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Preview;

/// <summary>
/// Export of what the Preview page shows: the displayed pixels as a PNG, and a plain-text
/// diagnostic of the parameters, confidence and warnings behind them.
/// </summary>
public static class PreviewExport
{
    /// <summary>
    /// Writes <paramref name="pixels"/> (opaque BGRA, <paramref name="size"/>²) as a PNG. The file
    /// is written under a hidden partial name and renamed once complete, so a failed or canceled
    /// export never leaves a truncated image at <paramref name="path"/>.
    /// </summary>
    public static void SavePng(string path, int size, byte[] pixels, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != (long)size * size * 4)
        {
            throw new ArgumentException("The pixels do not match the preview size.", nameof(pixels));
        }

        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.partial");
        try
        {
            byte[] png = RasterCodec.EncodePng(size, size, pixels, grayscale: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(partial, png);
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>A suggested file name: project, family, mode, view and size.</summary>
    public static string SuggestedFileName(string projectName, PreviewResult result, string? appearanceId)
    {
        string name = string.Concat((projectName.Length == 0 ? "disc" : projectName)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c));
        string view = appearanceId is null ? "data" : appearanceId;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}-{result.Family.ToString().ToLowerInvariant()}-{result.Mode.ToString().ToLowerInvariant()}-{view}-{result.Size}px.png");
    }

    /// <summary>
    /// Diagnostic text for support and comparison: invariant, one fact per line, stable keys.
    /// </summary>
    public static string Diagnostics(
        string projectName,
        PreviewResult result,
        string? appearanceId,
        bool fileSystemOverlay,
        bool usefulRingOverlay)
    {
        ArgumentNullException.ThrowIfNull(result);
        StringBuilder text = new();
        text.AppendLine("Disc Image Studio preview");
        Line("project", projectName);
        Line("family", result.Family.ToString());
        Line("mode", result.Mode.ToString());
        Line("view", appearanceId is null ? "data-map" : $"appearance:{appearanceId}");
        Line("confidence", result.Confidence.ToString());
        Line("size", result.Size.ToString(CultureInfo.InvariantCulture));
        Line("elapsedSeconds", result.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        Line("revision", result.Revision.ToString(CultureInfo.InvariantCulture));
        Line("overlays", $"fileSystem={(fileSystemOverlay ? "on" : "off")} usefulRing={(usefulRingOverlay ? "on" : "off")}");
        foreach (PreviewRingOverlay ring in result.Overlays)
        {
            Line(
                $"ring.{ring.Kind}",
                string.Create(CultureInfo.InvariantCulture, $"{ring.InnerRadiusMm:0.###}..{ring.OuterRadiusMm:0.###} mm"));
        }

        text.AppendLine("warnings:");
        if (result.Warnings.Count == 0)
        {
            text.AppendLine("  (none)");
        }

        foreach (PreviewWarning warning in result.Warnings)
        {
            text.AppendLine(warning.Detail is { Length: > 0 } detail ? $"  {warning.Code}: {detail}" : $"  {warning.Code}");
        }

        text.AppendLine("parameters:");
        foreach (PreviewParameter parameter in result.Parameters)
        {
            text.AppendLine($"  {parameter.Key} = {parameter.Value}");
        }

        return text.ToString();

        void Line(string key, string value) => text.AppendLine($"{key}: {value}");
    }
}
