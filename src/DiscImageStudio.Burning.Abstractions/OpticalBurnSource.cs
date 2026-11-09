using System.Buffers.Binary;
using System.Text;

namespace DiscImageStudio.Burning;

/// <summary>
/// What a burn reads: the exact bytes a backend expects for <see cref="Mode"/> (headerless
/// little-endian PCM for CD audio, 2048-byte sectors otherwise), how many, and a producer that
/// writes them. <see cref="ToRequest"/> turns it into an <see cref="OpticalBurnRequest"/>.
/// </summary>
public sealed record OpticalBurnSource(
    string Description,
    OpticalBurnMediaKind Mode,
    long ContentLength,
    Action<Stream, CancellationToken> Produce)
{
    public OpticalBurnRequest ToRequest(
        string deviceId,
        OpticalWriteSpeed? speed = null,
        string? expectedMediaIdentity = null,
        OpticalBurnBufferOptions? bufferOptions = null)
        => new(deviceId, Mode, ContentLength, Produce, speed, bufferOptions, expectedMediaIdentity);
}

/// <summary>
/// Sources read from a published artifact (Task 11): an ISO for data modes, a CD-DA track as a
/// WAVE file (the 44-byte header is skipped) or as a raw file (big-endian samples, swapped to
/// the little-endian order the recorders take). The file is checked when the source is made and
/// its length again while it is read, so a file changed in between fails instead of burning
/// something else.
/// </summary>
public static class OpticalBurnSources
{
    private const int WaveHeaderBytes = 44;
    private const int CopyBytes = 1024 * 1024;

    /// <summary>The burn mode an artifact is written with, from the disc family and the file name.</summary>
    public static OpticalBurnMediaKind? ModeFor(string family, string path)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return (family.ToLowerInvariant(), extension) switch
        {
            ("cd" or "compactdisc", ".wav" or ".raw") => OpticalBurnMediaKind.CdAudio,
            ("cd" or "compactdisc", ".iso") => OpticalBurnMediaKind.CdData,
            ("dvd", ".iso") => OpticalBurnMediaKind.DvdData,
            ("bd" or "bluray", ".iso") => OpticalBurnMediaKind.BluRayData,
            _ => null,
        };
    }

    public static OpticalBurnSource FromArtifact(string path, OpticalBurnMediaKind mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        FileInfo file = new(fullPath);
        if (!file.Exists)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The image file does not exist.");
        }

        string extension = file.Extension.ToLowerInvariant();
        bool wave = mode == OpticalBurnMediaKind.CdAudio && extension == ".wav";
        bool raw = mode == OpticalBurnMediaKind.CdAudio && extension == ".raw";
        long offset = 0;
        long length = file.Length;
        if (wave)
        {
            (offset, length) = WaveData(fullPath);
        }

        int sector = OpticalBurnMedia.GetSectorBytes(mode);
        if (length <= 0 || length % sector != 0)
        {
            throw new OpticalBurnException(
                OpticalBurnErrorCode.SourceFailed,
                $"The image holds {length} bytes, not a whole number of {sector}-byte sectors.");
        }

        long expectedFileLength = file.Length;
        return new OpticalBurnSource(
            file.Name,
            mode,
            length,
            (output, cancellationToken) => Copy(fullPath, expectedFileLength, offset, length, raw, output, cancellationToken));
    }

    private static void Copy(string path, long expectedFileLength, long offset, long length, bool swapSamples, Stream output, CancellationToken cancellationToken)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBytes, FileOptions.SequentialScan);
        if (input.Length != expectedFileLength)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The image file changed after it was checked.");
        }

        input.Position = offset;
        byte[] buffer = new byte[CopyBytes];
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The image file ended early.");
            }

            if (swapSamples)
            {
                if (read % 2 != 0)
                {
                    throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "A raw CD track must hold whole 16-bit samples.");
                }

                for (int i = 0; i < read; i += 2)
                {
                    (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
                }
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>Offset and length of the PCM data of a canonical 44-byte-header WAVE file.</summary>
    private static (long Offset, long Length) WaveData(string path)
    {
        byte[] header = new byte[WaveHeaderBytes];
        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "The WAVE file is too short.");
            }
        }

        bool canonical = Encoding.ASCII.GetString(header, 0, 4) == "RIFF"
                         && Encoding.ASCII.GetString(header, 8, 4) == "WAVE"
                         && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) == 1
                         && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) == 2
                         && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == 44100
                         && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) == 16
                         && Encoding.ASCII.GetString(header, 36, 4) == "data";
        if (!canonical)
        {
            throw new OpticalBurnException(OpticalBurnErrorCode.SourceFailed, "Only 44.1 kHz 16-bit stereo PCM WAVE files can be burned as CD audio.");
        }

        return (WaveHeaderBytes, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40)));
    }
}
