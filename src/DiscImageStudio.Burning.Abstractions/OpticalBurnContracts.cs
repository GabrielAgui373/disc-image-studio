namespace DiscImageStudio.Burning;

public enum OpticalBurnMediaKind
{
    CdAudio,
    DvdData,
    BluRayData,
    CdData,
}

/// <summary>Per-mode facts every backend shares: which modes are disc-at-once CD sessions, and the
/// size of the sectors a producer hands over.</summary>
public static class OpticalBurnMedia
{
    public static bool UsesCdDao(OpticalBurnMediaKind mediaKind)
        => mediaKind is OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData;

    public static int GetSectorBytes(OpticalBurnMediaKind mediaKind)
        => mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio => 2352,
            OpticalBurnMediaKind.CdData or OpticalBurnMediaKind.DvdData or OpticalBurnMediaKind.BluRayData => 2048,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), mediaKind, "Unknown optical media kind."),
        };
}

public sealed record OpticalBurnDevice(
    string Id,
    string DisplayName,
    IReadOnlyList<string> VolumePaths);

public sealed record OpticalWriteSpeed(
    int SectorsPerSecond,
    bool RotationTypeIsPureCav)
{
    private const double DvdBytesPerSecondAtOneX = 1_385_000.0;
    private const double BluRayBytesPerSecondAtOneX = 4_500_000.0;
    private const double CdSectorsPerSecondAtOneX = 75.0;

    public double GetMultiplier(OpticalBurnMediaKind mediaKind)
        => mediaKind switch
        {
            OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => SectorsPerSecond / CdSectorsPerSecondAtOneX,
            OpticalBurnMediaKind.DvdData => SectorsPerSecond * 2048.0 / DvdBytesPerSecondAtOneX,
            OpticalBurnMediaKind.BluRayData => SectorsPerSecond * 2048.0 / BluRayBytesPerSecondAtOneX,
            _ => throw new ArgumentOutOfRangeException(nameof(mediaKind), mediaKind, "Unknown optical media kind."),
        };

    public double GetMegabytesPerSecond(OpticalBurnMediaKind mediaKind)
    {
        int sectorBytes = OpticalBurnMedia.GetSectorBytes(mediaKind);
        return SectorsPerSecond * (double)sectorBytes / 1_000_000.0;
    }

    internal void Validate()
    {
        if (SectorsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SectorsPerSecond),
                "Write speed must be greater than zero sectors per second.");
        }
    }
}

public sealed record OpticalBurnProgress(
    string Stage,
    string Message,
    long CompletedBytes,
    long TotalBytes,
    double? BytesPerSecond = null)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)CompletedBytes / TotalBytes;
}

/// <summary>
/// CD audio producers supply headerless 44.1 kHz, 16-bit stereo little-endian PCM,
/// as required by IMAPI_CD_SECTOR_AUDIO. ContentLength excludes any WAV header.
/// CD data, DVD and Blu-ray producers supply complete 2048-byte logical data sectors.
/// The recording backend adds physical encoding and error correction.
/// <para><see cref="ExpectedMediaIdentity"/> is the <see cref="OpticalMediaState.Identity"/> the
/// user confirmed; a backend that can read it checks it again just before writing and fails
/// with <see cref="OpticalBurnErrorCode.MediaChanged"/> when the disc was swapped. The Windows
/// backend predates it and ignores it.</para>
/// </summary>
public sealed record OpticalBurnRequest(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long ContentLength,
    Action<Stream, CancellationToken> ProduceContent,
    OpticalWriteSpeed? WriteSpeed = null,
    OpticalBurnBufferOptions? BufferOptions = null,
    string? ExpectedMediaIdentity = null)
{
    public OpticalBurnBufferOptions EffectiveBufferOptions => BufferOptions
        ?? (MediaKind == OpticalBurnMediaKind.BluRayData
            ? OpticalBurnBufferOptions.BluRayDefault : OpticalBurnBufferOptions.Default);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        ArgumentNullException.ThrowIfNull(ProduceContent);
        int sectorBytes = OpticalBurnMedia.GetSectorBytes(MediaKind);
        if (ContentLength <= 0 || ContentLength % sectorBytes != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContentLength),
                $"Stream length must be a positive multiple of {sectorBytes} bytes.");
        }

        WriteSpeed?.Validate();
        EffectiveBufferOptions.Validate();
    }
}

public sealed record OpticalBurnResult(
    string DeviceId,
    OpticalBurnMediaKind MediaKind,
    long BytesWritten,
    TimeSpan Elapsed,
    OpticalWriteSpeed? ActualWriteSpeed = null);

/// <summary>Writes a request to a device. Implemented by every backend (Windows IMAPI/MMC, the
/// simulated one, and the Linux one once validated).</summary>
public interface IOpticalBurner
{
    Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(
        string deviceId,
        OpticalBurnMediaKind mediaKind,
        CancellationToken cancellationToken = default);

    Task<OpticalBurnResult> BurnAsync(
        OpticalBurnRequest request,
        IProgress<OpticalBurnProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The original Windows contract: a burner that also lists its recorders.</summary>
public interface IOpticalDiscBurner : IOpticalBurner
{
    Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);
}
