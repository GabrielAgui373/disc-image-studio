using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning.Linux;

/// <summary>The outcome of one SCSI command: SCSI status, transport status, sense data and the bytes read.</summary>
public sealed record ScsiResult(byte Status, ushort HostStatus, ushort DriverStatus, byte[] Sense, byte[] Data)
{
    public bool Succeeded => Status == 0 && HostStatus == 0 && (DriverStatus & 0x0f) == 0;

    /// <summary>Sense key, ASC and ASCQ (fixed or descriptor format), or −1 when absent.</summary>
    public (int Key, int Asc, int Ascq) SenseCode
    {
        get
        {
            if (Sense.Length >= 14 && (Sense[0] & 0x7f) is 0x70 or 0x71)
            {
                return (Sense[2] & 0x0f, Sense[12], Sense[13]);
            }

            if (Sense.Length >= 4 && (Sense[0] & 0x7f) is 0x72 or 0x73)
            {
                return (Sense[1] & 0x0f, Sense[2], Sense[3]);
            }

            return (-1, -1, -1);
        }
    }

    /// <summary>NOT READY / MEDIUM NOT PRESENT (02/3A/xx).</summary>
    public bool IsNoMedium => SenseCode is (2, 0x3a, _);

    /// <summary>NOT READY / becoming ready or operation in progress (02/04/xx).</summary>
    public bool IsBecomingReady => SenseCode is (2, 0x04, _);
}

/// <summary>Sends read-only MMC commands to one opened drive. The boundary lets the parsing be checked with recorded replies.</summary>
public interface IScsiTransport : IDisposable
{
    ScsiResult Read(byte[] cdb, int allocationLength, TimeSpan timeout);
}

/// <summary>A drive opened for writing (Task 13.5): read commands plus commands that send data.</summary>
public interface IScsiWriteTransport : IScsiTransport
{
    ScsiResult Send(byte[] cdb, byte[] data, int length, TimeSpan timeout);
}

/// <summary>Opens a drive for reading MMC state, or exclusively for a burn.</summary>
public interface IScsiTransportFactory
{
    /// <summary>
    /// Opens <paramref name="devicePath"/> read-write, non-blocking and exclusive (no mount or
    /// other burner can hold it meanwhile). Needs write access to the node (uaccess or the group).
    /// </summary>
    /// <exception cref="LinuxDeviceOpenException">The node cannot be opened; <see cref="LinuxDeviceOpenException.Errno"/> says why.</exception>
    IScsiWriteTransport OpenForWriting(string devicePath);

    /// <summary>Opens <paramref name="devicePath"/> read-only and non-blocking (works without a disc).</summary>
    /// <exception cref="LinuxDeviceOpenException">The node cannot be opened; <see cref="LinuxDeviceOpenException.Errno"/> says why.</exception>
    IScsiTransport OpenForReading(string devicePath);
}

public sealed class LinuxDeviceOpenException(string path, int errno)
    : IOException($"Cannot open {path} (errno {errno}).")
{
    public int Errno { get; } = errno;
}

/// <summary>
/// SG_IO on a <c>/dev/sr*</c> node. Read commands only: the kernel's SCSI command filter allows
/// GET CONFIGURATION, READ DISC/TRACK INFORMATION and friends on a read-only descriptor, so
/// reading the state needs no write access (Task 13.4).
/// </summary>
public sealed class SgIoTransportFactory : IScsiTransportFactory
{
    public IScsiTransport OpenForReading(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        int descriptor = LinuxNative.Open(devicePath, LinuxNative.ReadOnly | LinuxNative.NonBlocking | LinuxNative.CloseOnExec);
        if (descriptor < 0)
        {
            throw new LinuxDeviceOpenException(devicePath, LinuxNative.LastError);
        }

        return new SgIoTransport(descriptor);
    }

    public IScsiWriteTransport OpenForWriting(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        int descriptor = LinuxNative.Open(
            devicePath,
            LinuxNative.ReadWrite | LinuxNative.NonBlocking | LinuxNative.CloseOnExec | LinuxNative.Exclusive);
        if (descriptor < 0)
        {
            throw new LinuxDeviceOpenException(devicePath, LinuxNative.LastError);
        }

        return new SgIoTransport(descriptor);
    }
}

internal sealed class SgIoTransport(int descriptor) : IScsiWriteTransport
{
    private const int SenseBytes = 32;
    private int _descriptor = descriptor;

    public ScsiResult Read(byte[] cdb, int allocationLength, TimeSpan timeout)
        => Execute(cdb, new byte[allocationLength], allocationLength, toDevice: false, timeout);

    public ScsiResult Send(byte[] cdb, byte[] data, int length, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, data.Length);
        return Execute(cdb, data, length, toDevice: true, timeout);
    }

    private ScsiResult Execute(byte[] cdb, byte[] data, int allocationLength, bool toDevice, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(cdb);
        ObjectDisposedException.ThrowIf(_descriptor < 0, this);
        byte[] sense = new byte[SenseBytes];
        GCHandle cdbHandle = GCHandle.Alloc(cdb, GCHandleType.Pinned);
        GCHandle dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle senseHandle = GCHandle.Alloc(sense, GCHandleType.Pinned);
        try
        {
            SgIoHeader header = new()
            {
                interface_id = SgIoHeader.InterfaceId,
                dxfer_direction = allocationLength == 0 ? SgIoHeader.TransferNone
                    : toDevice ? SgIoHeader.TransferToDevice : SgIoHeader.TransferFromDevice,
                cmd_len = (byte)cdb.Length,
                mx_sb_len = SenseBytes,
                dxfer_len = (uint)allocationLength,
                dxferp = allocationLength > 0 ? dataHandle.AddrOfPinnedObject() : IntPtr.Zero,
                cmdp = cdbHandle.AddrOfPinnedObject(),
                sbp = senseHandle.AddrOfPinnedObject(),
                timeout = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue),
            };
            if (LinuxNative.IoControl(_descriptor, SgIoHeader.SgIo, ref header) != 0)
            {
                throw new IOException($"SG_IO failed (errno {LinuxNative.LastError}).");
            }

            int received = toDevice ? 0 : Math.Clamp(allocationLength - header.resid, 0, allocationLength);
            return new ScsiResult(header.status, header.host_status, header.driver_status, sense[..header.sb_len_wr], data[..received]);
        }
        finally
        {
            cdbHandle.Free();
            dataHandle.Free();
            senseHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_descriptor >= 0)
        {
            _ = LinuxNative.Close(_descriptor);
            _descriptor = -1;
        }
    }
}
