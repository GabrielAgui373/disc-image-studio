using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiscImageStudio.Burning.Linux;

/// <summary>
/// The few libc calls the Linux backend needs, all with fixed layouts on LP64 Linux
/// (x86_64 and aarch64): open/close, ioctl(SG_IO), statx and access. No shell, no helper
/// process and no elevated rights are involved.
/// </summary>
internal static partial class LinuxNative
{
    internal const int ReadOnly = 0;
    internal const int ReadWrite = 2;
    internal const int NonBlocking = 0x800;
    internal const int CloseOnExec = 0x80000;
    internal const int ReadAccess = 4;
    internal const int WriteAccess = 2;
    internal const int ErrorNoEntry = 2;
    internal const int ErrorAccess = 13;
    internal const int ErrorPermission = 1;
    internal const int ErrorBusy = 16;
    internal const int ErrorNoMedium = 123;
    internal const int ErrorNotTty = 25;

    private const int AtCurrentDirectory = -100;
    private const uint StatxBasicStats = 0x7ff;

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int IoControl(int descriptor, nuint request, ref SgIoHeader header);

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, [Out] byte[] buffer);

    /// <summary>Owner, group and permission bits of a path (<c>struct statx</c> is the same on every architecture).</summary>
    internal static (uint Uid, uint Gid, int Mode)? OwnerAndMode(string path)
    {
        byte[] buffer = new byte[256];
        if (Statx(AtCurrentDirectory, path, 0, StatxBasicStats, buffer) != 0)
        {
            return null;
        }

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24)),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28)));
    }

    internal static int LastError => Marshal.GetLastPInvokeError();
}

/// <summary><c>struct sg_io_hdr</c> from &lt;scsi/sg.h&gt; (88 bytes on LP64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SgIoHeader
{
    internal const nuint SgIo = 0x2285;
    internal const int InterfaceId = 'S';
    internal const int TransferNone = -1;
    internal const int TransferToDevice = -2;
    internal const int TransferFromDevice = -3;

    public int interface_id;
    public int dxfer_direction;
    public byte cmd_len;
    public byte mx_sb_len;
    public ushort iovec_count;
    public uint dxfer_len;
    public IntPtr dxferp;
    public IntPtr cmdp;
    public IntPtr sbp;
    public uint timeout;
    public uint flags;
    public int pack_id;
    public IntPtr usr_ptr;
    public byte status;
    public byte masked_status;
    public byte msg_status;
    public byte sb_len_wr;
    public ushort host_status;
    public ushort driver_status;
    public int resid;
    public uint duration;
    public uint info;
}
