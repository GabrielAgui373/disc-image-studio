namespace DiscImageStudio.Burning;

/// <summary>The MMC profile of the loaded disc, reduced to what the product writes.</summary>
public enum OpticalMediaProfile
{
    Unknown,
    CdRom,
    CdR,
    CdRw,
    DvdRom,
    DvdMinusR,
    DvdMinusRw,
    DvdPlusR,
    DvdPlusRw,
    DvdMinusRDualLayer,
    DvdPlusRDualLayer,
    BdRom,
    BdR,
    BdRe,
}

/// <summary>The disc in a recorder, as far as the backend could read it.</summary>
/// <param name="Identity">
/// A value that changes when the disc changes (for example profile, capacity and the media
/// serial or ATIP/ADIP data). A confirmation is tied to it; see
/// <see cref="OpticalBurnRequest.ExpectedMediaIdentity"/>.
/// </param>
/// <param name="CapacitySectors">Recordable 2048-byte logical sectors of a blank disc.</param>
/// <param name="FreeSectors">Sectors still writable; equals the capacity on a blank disc.</param>
public sealed record OpticalMediaState(
    OpticalMediaProfile Profile,
    bool IsBlank,
    bool IsAppendable,
    long CapacitySectors,
    long FreeSectors,
    string Identity)
{
    public bool IsCd => Profile is OpticalMediaProfile.CdRom or OpticalMediaProfile.CdR or OpticalMediaProfile.CdRw;

    public bool IsDvd => Profile is OpticalMediaProfile.DvdRom or OpticalMediaProfile.DvdMinusR or OpticalMediaProfile.DvdMinusRw
        or OpticalMediaProfile.DvdPlusR or OpticalMediaProfile.DvdPlusRw or OpticalMediaProfile.DvdMinusRDualLayer
        or OpticalMediaProfile.DvdPlusRDualLayer;

    public bool IsBluRay => Profile is OpticalMediaProfile.BdRom or OpticalMediaProfile.BdR or OpticalMediaProfile.BdRe;

    public bool IsWritable => Profile is not (OpticalMediaProfile.Unknown or OpticalMediaProfile.CdRom
        or OpticalMediaProfile.DvdRom or OpticalMediaProfile.BdRom);

    /// <summary>
    /// Bytes a request of <paramref name="mode"/> can hold. CD audio sectors carry 2352 bytes
    /// where a data sector carries 2048, so the same disc holds more audio bytes.
    /// </summary>
    public long FreeBytes(OpticalBurnMediaKind mode) => FreeSectors * OpticalBurnMedia.GetSectorBytes(mode);

    /// <summary>True when <paramref name="mode"/> belongs on this kind of disc.</summary>
    public bool Fits(OpticalBurnMediaKind mode) => mode switch
    {
        OpticalBurnMediaKind.CdAudio or OpticalBurnMediaKind.CdData => IsCd,
        OpticalBurnMediaKind.DvdData => IsDvd,
        OpticalBurnMediaKind.BluRayData => IsBluRay,
        _ => false,
    };
}

/// <summary>Whether the user can reach the recorder at all.</summary>
public enum OpticalDeviceAccess
{
    Ready,

    /// <summary>The device node exists but the user may not open it (group or rule missing).</summary>
    PermissionDenied,

    /// <summary>Another program holds the recorder.</summary>
    Busy,

    /// <summary>The device disappeared since it was listed.</summary>
    Missing,
}

/// <summary>Why a write mode cannot be used now. <see cref="None"/> means it can.</summary>
public enum OpticalBurnUnavailableReason
{
    None,
    BackendUnavailable,
    PermissionDenied,
    DeviceBusy,
    DeviceCannotWrite,
    NoMedia,
    MediaNotWritable,
    MediaNotBlank,
    MediaIncompatible,

    /// <summary>The backend could do it, but the mode was never validated on real hardware.</summary>
    NotValidated,
}

/// <summary>One write mode on one recorder with the loaded disc.</summary>
public sealed record OpticalModeCapability(
    OpticalBurnMediaKind Mode,
    OpticalBurnUnavailableReason Reason,
    string? Detail = null)
{
    public bool IsAvailable => Reason == OpticalBurnUnavailableReason.None;
}

/// <summary>Write modes a recorder supports in principle, independent of the loaded disc.</summary>
[Flags]
public enum OpticalWriteModes
{
    None = 0,
    CdAudio = 1 << 0,
    CdData = 1 << 1,
    DvdData = 1 << 2,
    BluRayData = 1 << 3,
    All = CdAudio | CdData | DvdData | BluRayData,
}

/// <summary>Device features beyond writing.</summary>
[Flags]
public enum OpticalDeviceFeatures
{
    None = 0,
    Eject = 1 << 0,
    Lock = 1 << 1,
    BufferUnderrunProtection = 1 << 2,
}

/// <summary>A recorder with what it can do right now and, if not, why.</summary>
public sealed record OpticalDeviceStatus(
    OpticalBurnDevice Device,
    string? Vendor,
    string? Model,
    string? Firmware,
    OpticalWriteModes WriteModes,
    OpticalDeviceFeatures Features,
    OpticalDeviceAccess Access,
    string? AccessDetail,
    OpticalMediaState? Media,
    IReadOnlyList<OpticalModeCapability> Modes)
{
    public OpticalModeCapability Mode(OpticalBurnMediaKind mode)
        => Modes.FirstOrDefault(capability => capability.Mode == mode)
           ?? new OpticalModeCapability(mode, OpticalBurnUnavailableReason.DeviceCannotWrite);

    /// <summary>
    /// The capability of every mode from the device's modes, its access and the loaded disc.
    /// Backends call this so the rules are the same everywhere; <paramref name="validated"/>
    /// lists the modes proven on real hardware (the rest report <see cref="OpticalBurnUnavailableReason.NotValidated"/>).
    /// </summary>
    public static IReadOnlyList<OpticalModeCapability> Evaluate(
        OpticalWriteModes writeModes,
        OpticalDeviceAccess access,
        OpticalMediaState? media,
        OpticalWriteModes validated)
    {
        List<OpticalModeCapability> modes = [];
        foreach (OpticalBurnMediaKind mode in Enum.GetValues<OpticalBurnMediaKind>())
        {
            OpticalWriteModes flag = Flag(mode);
            OpticalBurnUnavailableReason reason =
                (writeModes & flag) == 0 ? OpticalBurnUnavailableReason.DeviceCannotWrite
                : access == OpticalDeviceAccess.PermissionDenied ? OpticalBurnUnavailableReason.PermissionDenied
                : access is OpticalDeviceAccess.Busy or OpticalDeviceAccess.Missing ? OpticalBurnUnavailableReason.DeviceBusy
                : (validated & flag) == 0 ? OpticalBurnUnavailableReason.NotValidated
                : media is null ? OpticalBurnUnavailableReason.NoMedia
                : !media.Fits(mode) ? OpticalBurnUnavailableReason.MediaIncompatible
                : !media.IsWritable ? OpticalBurnUnavailableReason.MediaNotWritable
                : !media.IsBlank ? OpticalBurnUnavailableReason.MediaNotBlank
                : OpticalBurnUnavailableReason.None;
            modes.Add(new OpticalModeCapability(mode, reason));
        }

        return modes;
    }

    public static OpticalWriteModes Flag(OpticalBurnMediaKind mode) => mode switch
    {
        OpticalBurnMediaKind.CdAudio => OpticalWriteModes.CdAudio,
        OpticalBurnMediaKind.CdData => OpticalWriteModes.CdData,
        OpticalBurnMediaKind.DvdData => OpticalWriteModes.DvdData,
        OpticalBurnMediaKind.BluRayData => OpticalWriteModes.BluRayData,
        _ => OpticalWriteModes.None,
    };
}

/// <summary>Whether a backend can be used on this machine at all, and why not.</summary>
public sealed record OpticalBackendAvailability(
    bool IsAvailable,
    OpticalBurnUnavailableReason Reason,
    string? Detail = null)
{
    public static OpticalBackendAvailability Available { get; } = new(true, OpticalBurnUnavailableReason.None);
}

/// <summary>Lists recorders and reads their state without writing anything.</summary>
public interface IOpticalDeviceService
{
    /// <summary>A short, stable backend name for diagnostics ("windows-imapi", "simulated", ...).</summary>
    string BackendName { get; }

    OpticalBackendAvailability Availability { get; }

    Task<IReadOnlyList<OpticalDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one recorder again (the disc may have changed).</summary>
    Task<OpticalDeviceStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>Stable failure categories of a burn; the presentation layer localizes them.</summary>
public enum OpticalBurnErrorCode
{
    BackendUnavailable,
    DeviceNotFound,
    DeviceBusy,
    PermissionDenied,
    NoMedia,
    MediaNotBlank,
    MediaIncompatible,
    InsufficientCapacity,

    /// <summary>The disc differs from the one the user confirmed.</summary>
    MediaChanged,

    /// <summary>The recorder ran out of data while writing; the disc is usually lost.</summary>
    BufferUnderrun,

    SourceFailed,
    WriteFailed,
    Unsupported,
}

/// <summary>A burn failure with a stable code.</summary>
/// <param name="CleanupFailed">
/// After the failure or a cancellation the backend could not close or release the session:
/// the recorder may need a power cycle and the disc is not usable.
/// </param>
public sealed class OpticalBurnException(OpticalBurnErrorCode code, string message, bool cleanupFailed = false, Exception? inner = null)
    : IOException(message, inner)
{
    public OpticalBurnErrorCode Code { get; } = code;

    public bool CleanupFailed { get; } = cleanupFailed;
}

/// <summary>The cancellation of a burn could not clean up the session.</summary>
public sealed class OpticalBurnCleanupException(string message, Exception? inner = null)
    : OperationCanceledException(message, inner);

/// <summary>Stages a burn reports, as the <see cref="OpticalBurnProgress.Stage"/> strings.</summary>
public enum OpticalBurnStage
{
    Prepare,
    Media,
    Buffer,
    Write,
    Finalize,
    Cleanup,
    Eject,
    Unknown,
}

public static class OpticalBurnStages
{
    // The Windows backend reports "media", "write" and "finalize"; the names stay as they were.
    public const string Prepare = "prepare";
    public const string Media = "media";
    public const string Buffer = "buffer";
    public const string Write = "write";
    public const string Finalize = "finalize";
    public const string Cleanup = "cleanup";
    public const string Eject = "eject";

    public static OpticalBurnStage Parse(string? stage) => stage switch
    {
        Prepare => OpticalBurnStage.Prepare,
        Media => OpticalBurnStage.Media,
        Buffer => OpticalBurnStage.Buffer,
        Write => OpticalBurnStage.Write,
        Finalize => OpticalBurnStage.Finalize,
        Cleanup => OpticalBurnStage.Cleanup,
        Eject => OpticalBurnStage.Eject,
        _ => OpticalBurnStage.Unknown,
    };
}

/// <summary>The user's verdict on a burn made while a mode was enabled only for testing.</summary>
public sealed record OpticalValidationResult(
    OpticalDeviceStatus Device,
    OpticalBurnMediaKind Mode,
    OpticalMediaProfile Media,
    string? MediaBrand,
    string? Speed,
    bool Passed,
    string? Notes);

/// <summary>Records physical test results; a passing one enables the mode for that recorder and disc type.</summary>
public interface IOpticalValidationRecorder
{
    /// <summary>True when <paramref name="mode"/> is enabled on this recorder and disc only for testing.</summary>
    bool IsTestOnly(OpticalDeviceStatus device, OpticalBurnMediaKind mode);

    void Record(OpticalValidationResult result);
}
