using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Generation;

/// <summary>What publishing does when the destination already exists.</summary>
public enum ArtifactReplacePolicy
{
    /// <summary>Refuse with <c>generation.output.exists</c>; the caller asks the user first.</summary>
    Fail,

    /// <summary>
    /// Replace the artifact and its companions. The old files are kept as hidden backups until the
    /// new set is in place and restored if publishing fails.
    /// </summary>
    Replace,
}

/// <param name="WriteManifest">Also publish <c>&lt;artifact&gt;.manifest.json</c> with sizes and SHA-256 hashes.</param>
public sealed record ArtifactPublishOptions(
    ArtifactReplacePolicy ReplacePolicy = ArtifactReplacePolicy.Fail,
    bool WriteManifest = true);

/// <summary>A file of a published set, with the size and hash it was published with.</summary>
public sealed record PublishedFile(string Path, string Kind, long Bytes, string Sha256);

/// <summary>The published artifact and its companions (sidecars, cue sheet, manifest).</summary>
public sealed record PublishedArtifact(PublishedFile Artifact, IReadOnlyList<PublishedFile> Companions, string? ManifestPath);

/// <summary>
/// The checks that can be made before a long generation: the destination, whether it exists,
/// the space needed against the space free, and problems that would stop the job.
/// </summary>
/// <param name="RequiredBytes">Artifact plus a margin for companions; the old artifact stays until commit.</param>
/// <param name="AvailableBytes">Free space for the destination, or <see langword="null"/> when unknown.</param>
public sealed record OutputPreflight(
    string TargetPath,
    bool TargetExists,
    IReadOnlyList<string> ExistingCompanions,
    long RequiredBytes,
    long? AvailableBytes,
    IReadOnlyList<GenerationValidationError> Problems)
{
    public bool CanStart => Problems.Count == 0;
}

/// <summary>
/// Integrity record published next to an artifact as <c>&lt;artifact&gt;.manifest.json</c>. The
/// engines' sidecars keep their formats; this file adds what they lack: the exact size and
/// SHA-256 of the artifact and of every companion, and what produced them.
/// </summary>
public sealed record ArtifactManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("artifact")] ArtifactManifestFile Artifact,
    [property: JsonPropertyName("companions")] IReadOnlyList<ArtifactManifestFile> Companions,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("filesystem")] string Filesystem,
    [property: JsonPropertyName("drawingStartSector")] long DrawingStartSector,
    [property: JsonPropertyName("drawingEndSectorExclusive")] long DrawingEndSectorExclusive,
    [property: JsonPropertyName("generatedUtc")] DateTimeOffset GeneratedUtc,
    [property: JsonPropertyName("generator")] string Generator)
{
    public const string FormatName = "disc-image-studio-artifact-manifest-v1";
    public const string Suffix = ".manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string PathFor(string artifactPath) => artifactPath + Suffix;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine;

    /// <summary>Reads the manifest next to <paramref name="artifactPath"/>; <see langword="null"/> if absent or not this format.</summary>
    public static ArtifactManifest? TryLoad(string artifactPath)
    {
        string path = PathFor(artifactPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            ArtifactManifest? manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(path), JsonOptions);
            return manifest?.Format == FormatName ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string GeneratorName()
    {
        Assembly assembly = typeof(ArtifactManifest).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "unknown";
        return $"DiscImageStudio.Generation {version}";
    }
}

public sealed record ArtifactManifestFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>Write-only pass-through that hashes and counts every byte on its way to the file.</summary>
internal sealed class HashingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    public string HashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
