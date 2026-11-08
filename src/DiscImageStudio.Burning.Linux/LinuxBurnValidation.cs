using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Burning.Linux;

/// <summary>
/// One recorded physical test (Task 13.5): the recorder, its firmware, the disc, the speed and
/// the result. A mode is enabled on Linux only for a recorder and disc type with a passing record.
/// </summary>
public sealed record BurnValidationRecord(
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("firmware")] string Firmware,
    [property: JsonPropertyName("mode")] OpticalBurnMediaKind Mode,
    [property: JsonPropertyName("media")] OpticalMediaProfile Media,
    [property: JsonPropertyName("mediaBrand")] string? MediaBrand,
    [property: JsonPropertyName("speed")] string? Speed,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("testedUtc")] DateTimeOffset TestedUtc,
    [property: JsonPropertyName("notes")] string? Notes)
{
    public bool Passed => string.Equals(Result, "pass", StringComparison.OrdinalIgnoreCase);
}

public sealed record BurnValidationDocument(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("validations")] IReadOnlyList<BurnValidationRecord> Validations)
{
    public const string FormatName = "disc-image-studio-burn-validations-v1";
}

/// <summary>
/// Which write modes are proven on which recorder (Task 13.5). The records live in a JSON file
/// written by hand after a physical test (<c>~/.config/disc-image-studio/burn-validations.json</c>);
/// nothing in the app adds to it on its own. For the test campaign itself, the environment variable
/// <c>DISC_IMAGE_STUDIO_BURN_TEST_MODES</c> (for example <c>DvdData,BluRayData</c>) enables modes
/// that are not validated yet, and the page says so for each of them.
/// </summary>
public sealed class LinuxBurnValidation : IOpticalValidationRecorder
{
    public const string TestModesVariable = "DISC_IMAGE_STUDIO_BURN_TEST_MODES";

    /// <summary>Modes the Linux writer implements (all four, each still gated by a recorded test).</summary>
    public const OpticalWriteModes ImplementedModes = OpticalWriteModes.All;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<BurnValidationRecord> _records;
    private readonly Lock _gate = new();

    public LinuxBurnValidation(IEnumerable<BurnValidationRecord>? records = null, OpticalWriteModes testModes = OpticalWriteModes.None, string? path = null)
    {
        _records = [.. records ?? []];
        TestModes = testModes & ImplementedModes;
        Path = path;
    }

    /// <summary>The records file; results recorded from the app are saved here.</summary>
    public string? Path { get; }

    /// <summary>Modes enabled for a hardware test without a passing record.</summary>
    public OpticalWriteModes TestModes { get; }

    public IReadOnlyList<BurnValidationRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>Reads the records file (missing means no record) and the test variable.</summary>
    public static LinuxBurnValidation Load(string? path, string? testModes = null)
    {
        List<BurnValidationRecord> records = [];
        if (path is not null && File.Exists(path))
        {
            try
            {
                BurnValidationDocument? document = JsonSerializer.Deserialize<BurnValidationDocument>(File.ReadAllText(path), JsonOptions);
                if (document?.Format == BurnValidationDocument.FormatName)
                {
                    records.AddRange(document.Validations);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable file enables nothing.
            }
        }

        return new LinuxBurnValidation(records, ParseModes(testModes), path);
    }

    public static OpticalWriteModes ParseModes(string? text)
    {
        OpticalWriteModes modes = OpticalWriteModes.None;
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(part, ignoreCase: true, out OpticalBurnMediaKind mode))
            {
                modes |= OpticalDeviceStatus.Flag(mode);
            }
        }

        return modes;
    }

    /// <summary>Modes with a passing record for this recorder (vendor, model, firmware) and the loaded disc type.</summary>
    public OpticalWriteModes Validated(LinuxOpticalNode node, OpticalMediaState? media)
    {
        ArgumentNullException.ThrowIfNull(node);
        OpticalWriteModes modes = OpticalWriteModes.None;
        foreach (BurnValidationRecord record in Records)
        {
            if (record.Passed
                && Same(record.Vendor, node.Vendor) && Same(record.Model, node.Model) && Same(record.Firmware, node.Revision)
                && media is not null && record.Media == media.Profile)
            {
                modes |= OpticalDeviceStatus.Flag(record.Mode);
            }
        }

        return (modes & ImplementedModes) | TestModes;
    }

    public bool IsTestOnly(LinuxOpticalNode node, OpticalMediaState? media, OpticalBurnMediaKind mode)
    {
        OpticalWriteModes flag = OpticalDeviceStatus.Flag(mode);
        return (TestModes & flag) != 0 && (new LinuxBurnValidation(Records).Validated(node, media) & flag) == 0;
    }

    public bool IsTestOnly(OpticalDeviceStatus device, OpticalBurnMediaKind mode)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsTestOnly(Node(device), device.Media, mode);
    }

    /// <summary>Adds a physical test result and saves the file; a passing one enables the mode at once.</summary>
    public void Record(OpticalValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        BurnValidationRecord record = new(
            result.Device.Vendor ?? string.Empty,
            result.Device.Model ?? string.Empty,
            result.Device.Firmware ?? string.Empty,
            result.Mode,
            result.Media,
            string.IsNullOrWhiteSpace(result.MediaBrand) ? null : result.MediaBrand.Trim(),
            result.Speed,
            result.Passed ? "pass" : "fail",
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(result.Notes) ? null : result.Notes.Trim());
        lock (_gate)
        {
            _records.Add(record);
            if (Path is { } path)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                string temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, $".dis-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporary, JsonSerializer.Serialize(new BurnValidationDocument(BurnValidationDocument.FormatName, [.. _records]), JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }
        }
    }

    private static LinuxOpticalNode Node(OpticalDeviceStatus device)
        => new(device.Device.Id, device.Device.VolumePaths.FirstOrDefault() ?? string.Empty, device.Vendor, device.Model, device.Firmware, -1, -1, new Dictionary<string, string>());

    /// <summary>An empty records file, to fill in after a test.</summary>
    public static string Template()
        => JsonSerializer.Serialize(
            new BurnValidationDocument(
                BurnValidationDocument.FormatName,
                [new BurnValidationRecord("VENDOR", "MODEL", "FIRMWARE", OpticalBurnMediaKind.DvdData, OpticalMediaProfile.DvdPlusR, "brand", "8x", "pass", DateTimeOffset.UnixEpoch, "how the disc looked")]),
            JsonOptions);

    private static bool Same(string expected, string? actual)
        => string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line of the local burn log: what was attempted on which hardware and how it ended.</summary>
public sealed record BurnTestLogEntry(
    [property: JsonPropertyName("utc")] DateTimeOffset Utc,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("vendor")] string? Vendor,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("firmware")] string? Firmware,
    [property: JsonPropertyName("media")] OpticalMediaProfile? Media,
    [property: JsonPropertyName("mode")] OpticalBurnMediaKind Mode,
    [property: JsonPropertyName("speedSectorsPerSecond")] int? Speed,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("totalBytes")] long TotalBytes,
    [property: JsonPropertyName("elapsedSeconds")] double ElapsedSeconds,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("sense")] string? Sense,
    [property: JsonPropertyName("testOnly")] bool TestOnly);

/// <summary>
/// Appends every Linux burn to a local JSON-lines log (<c>~/.local/state/disc-image-studio/burn-tests.jsonl</c>),
/// the raw material for the physical matrix. Paths and file names are not recorded.
/// </summary>
public sealed class BurnTestLog(string? path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _gate = new();

    public string? Path { get; } = path;

    public void Append(BurnTestLogEntry entry)
    {
        if (Path is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The log is diagnostics; failing to write it must not change the burn's outcome.
        }
    }
}
