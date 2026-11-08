using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Dvd;

namespace DiscImageStudio.Generation;

/// <summary>One check of a generated artifact and whether it passed.</summary>
public sealed record ArtifactCheck(string Code, bool Passed, string? Detail = null);

/// <summary>The checks run on an artifact; publication proceeds only when all passed.</summary>
public sealed record ArtifactValidationReport(IReadOnlyList<ArtifactCheck> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);

    public ArtifactCheck? FirstFailure => Checks.FirstOrDefault(check => !check.Passed);
}

/// <summary>A staged artifact failed validation; nothing was published.</summary>
public sealed class ArtifactValidationException(ArtifactValidationReport report)
    : IOException($"Artifact validation failed: {report.FirstFailure?.Code} {report.FirstFailure?.Detail}")
{
    public ArtifactValidationReport Report { get; } = report;
}

/// <summary>
/// Post-generation checks per format (Task 11.4), run on the staged set before it is published:
/// size against the plan, the container (WAVE header, raw CD frame multiple, ISO 9660 + Joliet
/// descriptors, UDF volume recognition), the sidecar read back by its engine reader and matched
/// with the plan, and optionally a second read of the artifact against the hash taken while it
/// was written.
/// </summary>
public static class ArtifactValidator
{
    private const int LogicalSectorBytes = 2048;

    public static ArtifactValidationReport Validate(StagedArtifactSet set, bool verifyHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        GenerationPlan plan = set.Plan;
        string path = set.Artifact.StagedPath;
        long length = new FileInfo(path).Length;
        List<ArtifactCheck> checks =
        [
            new("size", length == plan.EstimatedBytes, $"{length} of {plan.EstimatedBytes} bytes"),
        ];

        checks.AddRange(ContainerChecks(plan, path, length));
        checks.Add(SidecarCheck(plan, path));
        cancellationToken.ThrowIfCancellationRequested();
        if (verifyHash)
        {
            checks.Add(HashCheck(path, set.Artifact.Final.Sha256, cancellationToken));
            foreach (StagedFile companion in set.Companions)
            {
                checks.Add(HashCheck(companion.StagedPath, companion.Final.Sha256, cancellationToken, $"hash.{companion.Final.Kind}"));
            }
        }

        return new ArtifactValidationReport(checks);
    }

    private static IEnumerable<ArtifactCheck> ContainerChecks(GenerationPlan plan, string path, long length)
    {
        switch (plan.Filesystem)
        {
            case GenerationFilesystem.CdDa when Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase):
                yield return WaveCheck(path, length);
                break;
            case GenerationFilesystem.CdDa:
                yield return new ArtifactCheck("raw.frames", length % CdDiscParameters.BytesPerSector == 0, $"{length} bytes");
                break;
            case GenerationFilesystem.Iso9660Joliet:
                byte[] primary = ReadSector(path, 16);
                byte[] joliet = ReadSector(path, 17);
                byte[] terminator = ReadSector(path, 18);
                yield return new ArtifactCheck("iso9660.primary", IsDescriptor(primary, 1));
                yield return new ArtifactCheck("iso9660.joliet", IsDescriptor(joliet, 2) && joliet[88] == (byte)'%');
                yield return new ArtifactCheck("iso9660.terminator", IsDescriptor(terminator, 255));
                break;
            case GenerationFilesystem.Udf201:
                yield return new ArtifactCheck(
                    "udf.recognition",
                    HasIdentifier(ReadSector(path, 16), "BEA01")
                    && HasIdentifier(ReadSector(path, 17), "NSR03")
                    && HasIdentifier(ReadSector(path, 18), "TEA01"));
                break;
        }
    }

    private static ArtifactCheck WaveCheck(string path, long length)
    {
        byte[] header = new byte[44];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        bool riff = Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WAVE";
        bool pcm = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) == 1
                   && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) == 2
                   && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == 44100
                   && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) == 16;
        bool data = Encoding.ASCII.GetString(header, 36, 4) == "data"
                    && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)) == length - 44;
        return new ArtifactCheck("wave.header", riff && pcm && data);
    }

    /// <summary>The sidecar next to the staged artifact, read by its engine reader, describes the plan.</summary>
    private static ArtifactCheck SidecarCheck(GenerationPlan plan, string path)
    {
        bool matches = plan.Details switch
        {
            CdGenerationPlanDetails { Request.Format: CdArtifactFormat.Mode1Iso } cd =>
                CdDataImageMetadata.TryLoad(path) is { } data
                && data.Parameters.Sectors == cd.Request.Parameters.Sectors
                && data.DrawingStartLba == plan.DrawingRange.StartSector,
            CdGenerationPlanDetails cd =>
                CdTrackMetadata.TryLoad(path) is { } track
                && track.Sectors == cd.Request.Parameters.Sectors
                && track.Interleaved == cd.Request.Interleave,
            DvdGenerationPlanDetails dvd =>
                DvdImageMetadata.TryLoad(path) is { } image
                && image.TotalSectors == dvd.Request.Options.TotalSectors,
            BluRayGenerationPlanDetails bluRay =>
                BluRayIsoMetadata.TryLoad(path) is { } iso
                && iso.TotalSectors == bluRay.Request.Parameters.TotalSectors
                && iso.DrawingStartSector == plan.DrawingRange.StartSector,
            CalibrationTargetPlanDetails calibration => CalibrationSnapshotMatches(calibration, path),
            _ => true,
        };
        return new ArtifactCheck("sidecar", matches);
    }

    /// <summary>The published snapshot reads back as the session the target was drawn from.</summary>
    private static bool CalibrationSnapshotMatches(CalibrationTargetPlanDetails details, string path)
    {
        try
        {
            DiscImageStudio.Core.Calibration.CalibrationSession snapshot = DiscImageStudio.Core.Calibration.CalibrationSessionJson.Load(
                Path.ChangeExtension(path, DiscGenerationService.CalibrationSidecarSuffix));
            return snapshot.Target.Id == details.Request.Session.Target.Id
                   && snapshot.Target.Parameters == details.Request.Session.Target.Parameters;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static ArtifactCheck HashCheck(string path, string expected, CancellationToken cancellationToken, string code = "hash.artifact")
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return new ArtifactCheck(code, Convert.ToHexStringLower(hash.GetHashAndReset()) == expected);
    }

    private static byte[] ReadSector(string path, int lba)
    {
        byte[] sector = new byte[LogicalSectorBytes];
        using FileStream stream = File.OpenRead(path);
        if (stream.Length < (long)(lba + 1) * LogicalSectorBytes)
        {
            return sector;
        }

        stream.Position = (long)lba * LogicalSectorBytes;
        stream.ReadExactly(sector);
        return sector;
    }

    private static bool IsDescriptor(byte[] sector, byte type) => sector[0] == type && HasIdentifier(sector, "CD001");

    private static bool HasIdentifier(byte[] sector, string identifier)
        => Encoding.ASCII.GetString(sector, 1, 5) == identifier;
}

/// <summary>
/// Identity of a data folder's content: relative paths, sizes and modification times. Equal
/// fingerprints mean the folder still holds what the plan laid out.
/// </summary>
public static class DataFolderFingerprint
{
    public static string? Compute(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string root = Path.GetFullPath(directory);
        StringBuilder text = new();
        foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                     .Order(StringComparer.Ordinal))
        {
            FileInfo info = new(file);
            text.Append(Path.GetRelativePath(root, file)).Append('|').Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
