using System.Globalization;

namespace DiscImageStudio.Burning.Linux;

/// <summary>Why the current user can or cannot use a drive node, without changing anything.</summary>
/// <param name="GroupName">The group that owns the node (usually <c>cdrom</c>), when it can be read.</param>
/// <param name="InGroup">The user belongs to that group.</param>
public sealed record LinuxAccessDiagnosis(
    OpticalDeviceAccess Access,
    bool Exists,
    bool Readable,
    bool Writable,
    string? GroupName,
    int Mode,
    bool InGroup)
{
    /// <summary>An explanation for the page: what is missing and how it is normally granted. Nothing is changed for the user.</summary>
    public string? Explanation(string devicePath)
    {
        if (!Exists)
        {
            return $"{devicePath} is gone; the drive was disconnected.";
        }

        if (Writable)
        {
            return null;
        }

        string group = GroupName ?? "the group that owns it";
        string rights = Readable ? "can read the drive but not write to it" : "cannot open the drive";
        return $"This user {rights} ({devicePath}, group {group}, mode {Convert.ToString(Mode & 0x1ff, 8).PadLeft(3, '0')}). "
               + "Desktop sessions on this computer get access automatically through udev (uaccess); "
               + (InGroup
                   ? "the user is already in that group, so log out and in again."
                   : $"from other sessions, an administrator can add the user to the '{group}' group, then log in again.")
               + " The app never asks for root.";
    }
}

/// <summary>
/// The Linux device service of Task 13.4: drives from sysfs and udev, the current user's access
/// from the node itself, and the drive's write features and the loaded disc from read-only MMC
/// commands over SG_IO. When the node cannot be opened the udev data (<c>cdrom_id</c>) still tells
/// the drive's abilities and the disc type. Nothing is written, locked or ejected. Every write mode
/// is reported as not validated: modes are enabled only after a recorded physical test (Task 13.5).
/// </summary>
public sealed class LinuxOpticalDeviceService : IOpticalDeviceService
{
    private readonly LinuxDeviceTree _tree;
    private readonly IScsiTransportFactory _transports;
    private readonly OpticalWriteModes _validated;
    private readonly Func<string, LinuxAccessDiagnosis> _diagnose;

    public LinuxOpticalDeviceService(
        LinuxDeviceTree? tree = null,
        IScsiTransportFactory? transports = null,
        OpticalWriteModes validated = OpticalWriteModes.None,
        Func<string, LinuxAccessDiagnosis>? diagnose = null)
    {
        _tree = tree ?? new LinuxDeviceTree();
        _transports = transports ?? new SgIoTransportFactory();
        _validated = validated;
        _diagnose = diagnose ?? (path => Diagnose(path, _tree));
    }

    public string BackendName => "linux-sgio";

    public OpticalBackendAvailability Availability => OperatingSystem.IsLinux()
        ? OpticalBackendAvailability.Available
        : new OpticalBackendAvailability(false, OpticalBurnUnavailableReason.BackendUnavailable, "This backend runs on Linux only.");

    public Task<IReadOnlyList<OpticalDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<OpticalDeviceStatus>>(
            () => [.. _tree.Enumerate().Select(node =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Describe(node);
            })],
            cancellationToken);

    public Task<OpticalDeviceStatus> GetStatusAsync(string deviceId, CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                LinuxOpticalNode node = _tree.Enumerate().FirstOrDefault(item => Id(item) == deviceId)
                                        ?? throw new OpticalBurnException(OpticalBurnErrorCode.DeviceNotFound, "The drive is no longer listed.");
                return Describe(node);
            },
            cancellationToken);

    /// <summary>The stable id of a drive: its kernel name (<c>sr0</c>).</summary>
    public static string Id(LinuxOpticalNode node) => node.Name;

    private OpticalDeviceStatus Describe(LinuxOpticalNode node)
    {
        LinuxAccessDiagnosis access = _diagnose(node.DevicePath);
        OpticalWriteModes writeModes = UdevWriteModes(node);
        OpticalMediaState? media = null;
        string? detail = access.Explanation(node.DevicePath);
        OpticalDeviceAccess state = access.Access;
        if (access.Readable)
        {
            try
            {
                using IScsiTransport transport = _transports.OpenForReading(node.DevicePath);
                (OpticalWriteModes features, media) = MmcState.Read(transport);
                writeModes = features;
            }
            catch (LinuxDeviceOpenException exception) when (exception.Errno == LinuxNative.ErrorBusy)
            {
                state = OpticalDeviceAccess.Busy;
                detail = "Another program holds the drive.";
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // The drive answered oddly or is still spinning up: fall back to what udev recorded.
                media = UdevMedia(node);
                detail ??= $"The drive state could not be read directly ({exception.Message}); showing what udev recorded.";
            }
        }
        else
        {
            media = UdevMedia(node);
        }

        return new OpticalDeviceStatus(
            new OpticalBurnDevice(Id(node), DisplayName(node), [node.DevicePath]),
            node.Vendor,
            node.Model,
            node.Revision,
            writeModes,
            OpticalDeviceFeatures.Eject | OpticalDeviceFeatures.Lock,
            state,
            detail,
            media,
            OpticalDeviceStatus.Evaluate(writeModes, state, media, _validated));
    }

    private static string DisplayName(LinuxOpticalNode node)
    {
        string name = string.Join(" ", new[] { node.Vendor, node.Model }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return name.Length == 0 ? node.Name : $"{name} ({node.Name})";
    }

    /// <summary>Drive abilities as <c>cdrom_id</c> recorded them.</summary>
    public static OpticalWriteModes UdevWriteModes(LinuxOpticalNode node)
    {
        OpticalWriteModes modes = OpticalWriteModes.None;
        if (node.Flag("ID_CDROM_CD_R") || node.Flag("ID_CDROM_CD_RW"))
        {
            modes |= OpticalWriteModes.CdAudio | OpticalWriteModes.CdData;
        }

        if (node.Flag("ID_CDROM_DVD_R") || node.Flag("ID_CDROM_DVD_RW") || node.Flag("ID_CDROM_DVD_PLUS_R") || node.Flag("ID_CDROM_DVD_PLUS_RW"))
        {
            modes |= OpticalWriteModes.DvdData;
        }

        if (node.Flag("ID_CDROM_BD_R") || node.Flag("ID_CDROM_BD_RE"))
        {
            modes |= OpticalWriteModes.BluRayData;
        }

        return modes;
    }

    /// <summary>The disc as <c>cdrom_id</c> recorded it; capacity is unknown this way.</summary>
    public static OpticalMediaState? UdevMedia(LinuxOpticalNode node)
    {
        if (!node.Flag("ID_CDROM_MEDIA"))
        {
            return null;
        }

        (string Key, OpticalMediaProfile Profile)[] table =
        [
            ("ID_CDROM_MEDIA_BD_RE", OpticalMediaProfile.BdRe),
            ("ID_CDROM_MEDIA_BD_R", OpticalMediaProfile.BdR),
            ("ID_CDROM_MEDIA_BD", OpticalMediaProfile.BdRom),
            ("ID_CDROM_MEDIA_DVD_PLUS_R_DL", OpticalMediaProfile.DvdPlusRDualLayer),
            ("ID_CDROM_MEDIA_DVD_PLUS_RW", OpticalMediaProfile.DvdPlusRw),
            ("ID_CDROM_MEDIA_DVD_PLUS_R", OpticalMediaProfile.DvdPlusR),
            ("ID_CDROM_MEDIA_DVD_RW", OpticalMediaProfile.DvdMinusRw),
            ("ID_CDROM_MEDIA_DVD_R", OpticalMediaProfile.DvdMinusR),
            ("ID_CDROM_MEDIA_DVD", OpticalMediaProfile.DvdRom),
            ("ID_CDROM_MEDIA_CD_RW", OpticalMediaProfile.CdRw),
            ("ID_CDROM_MEDIA_CD_R", OpticalMediaProfile.CdR),
            ("ID_CDROM_MEDIA_CD", OpticalMediaProfile.CdRom),
        ];
        OpticalMediaProfile profile = table.FirstOrDefault(entry => node.Flag(entry.Key)).Profile;
        string state = node.UdevProperties.GetValueOrDefault("ID_CDROM_MEDIA_STATE", string.Empty);
        bool blank = state == "blank";
        string identity = "udev-" + string.Join(
            ',',
            node.UdevProperties.Where(pair => pair.Key.StartsWith("ID_CDROM_MEDIA", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        return new OpticalMediaState(profile, blank, blank || state == "appendable", 0, 0, identity);
    }

    /// <summary>Reads the node's owner, mode and the user's rights; changes nothing.</summary>
    public static LinuxAccessDiagnosis Diagnose(string devicePath, LinuxDeviceTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!File.Exists(devicePath) && LinuxNative.OwnerAndMode(devicePath) is null)
        {
            return new LinuxAccessDiagnosis(OpticalDeviceAccess.Missing, false, false, false, null, 0, false);
        }

        bool readable = LinuxNative.Access(devicePath, LinuxNative.ReadAccess) == 0;
        bool writable = LinuxNative.Access(devicePath, LinuxNative.WriteAccess) == 0;
        (uint Uid, uint Gid, int Mode)? owner = LinuxNative.OwnerAndMode(devicePath);
        string? group = owner is { } o ? GroupName(tree, o.Gid) : null;
        bool inGroup = owner is { } g && UserGroups().Contains(g.Gid);
        OpticalDeviceAccess access = readable && writable ? OpticalDeviceAccess.Ready : OpticalDeviceAccess.PermissionDenied;
        return new LinuxAccessDiagnosis(access, true, readable, writable, group, owner?.Mode ?? 0, inGroup);
    }

    private static string? GroupName(LinuxDeviceTree tree, uint gid)
    {
        try
        {
            foreach (string line in File.ReadLines(tree.Resolve("/etc/group")))
            {
                string[] fields = line.Split(':');
                if (fields.Length >= 3 && uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id == gid)
                {
                    return fields[0];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>The process's supplementary groups, from <c>/proc/self/status</c>.</summary>
    private static HashSet<uint> UserGroups()
    {
        HashSet<uint> groups = [];
        try
        {
            string? line = File.ReadLines("/proc/self/status").FirstOrDefault(text => text.StartsWith("Groups:", StringComparison.Ordinal));
            foreach (string part in (line?[7..] ?? string.Empty).Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id))
                {
                    groups.Add(id);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return groups;
    }
}
