using System.Globalization;

namespace DiscImageStudio.Burning.Linux;

/// <summary>An optical drive as the kernel and udev describe it, before it is opened.</summary>
/// <param name="Name">Kernel name, such as <c>sr0</c>.</param>
/// <param name="DevicePath">The device node, such as <c>/dev/sr0</c> (under the tree's root).</param>
/// <param name="UdevProperties">
/// The udev database entry: <c>cdrom_id</c> fills <c>ID_CDROM_*</c> (drive abilities and the
/// loaded disc) without the user opening the device.
/// </param>
public sealed record LinuxOpticalNode(
    string Name,
    string DevicePath,
    string? Vendor,
    string? Model,
    string? Revision,
    int Major,
    int Minor,
    IReadOnlyDictionary<string, string> UdevProperties)
{
    public bool Flag(string key) => UdevProperties.TryGetValue(key, out string? value) && value == "1";
}

/// <summary>
/// Lists optical drives from sysfs (<c>/sys/class/block/sr*</c>, SCSI peripheral type 5) and
/// their udev entries (<c>/run/udev/data/b&lt;major&gt;:&lt;minor&gt;</c>). Nothing is opened. The
/// root is configurable so the reading can be checked against a recorded tree; the order is the
/// kernel number, so the list is the same on every call.
/// </summary>
public sealed class LinuxDeviceTree(string root = "/")
{
    /// <summary>SCSI peripheral device type of CD/DVD/BD drives.</summary>
    public const int ScsiTypeOptical = 5;

    public string Root { get; } = root ?? throw new ArgumentNullException(nameof(root));

    public string Resolve(string absolute) => Path.Combine(Root, absolute.TrimStart('/'));

    public IReadOnlyList<LinuxOpticalNode> Enumerate()
    {
        string classBlock = Resolve("/sys/class/block");
        if (!Directory.Exists(classBlock))
        {
            return [];
        }

        List<LinuxOpticalNode> nodes = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(classBlock, "sr*"))
        {
            string name = Path.GetFileName(entry);
            if (!int.TryParse(name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            string device = Path.Combine(entry, "device");
            if (ReadInt(Path.Combine(device, "type")) is { } type && type != ScsiTypeOptical)
            {
                continue;
            }

            (int major, int minor) = ReadDevNumbers(Path.Combine(entry, "dev"));
            Dictionary<string, string> udev = ReadUdev(major, minor);
            string devName = udev.TryGetValue("DEVNAME", out string? fromUdev) ? fromUdev : $"/dev/{name}";
            nodes.Add(new LinuxOpticalNode(
                name,
                Resolve(devName),
                ReadText(Path.Combine(device, "vendor")),
                ReadText(Path.Combine(device, "model")),
                ReadText(Path.Combine(device, "rev")),
                major,
                minor,
                udev));
        }

        return [.. nodes.OrderBy(node => int.Parse(node.Name.AsSpan(2), CultureInfo.InvariantCulture))];
    }

    private Dictionary<string, string> ReadUdev(int major, int minor)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        if (major < 0)
        {
            return properties;
        }

        string path = Resolve($"/run/udev/data/b{major}:{minor}");
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                // "E:KEY=VALUE" lines are the environment udev keeps for the device.
                if (line.StartsWith("E:", StringComparison.Ordinal) && line.IndexOf('=') is > 2 and int equals)
                {
                    properties[line[2..equals]] = line[(equals + 1)..];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return properties;
    }

    private static (int Major, int Minor) ReadDevNumbers(string path)
    {
        string? text = ReadText(path);
        if (text is null || text.Split(':') is not [string major, string minor])
        {
            return (-1, -1);
        }

        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out int a)
               && int.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out int b)
            ? (a, b)
            : (-1, -1);
    }

    private static int? ReadInt(string path)
        => int.TryParse(ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static string? ReadText(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
