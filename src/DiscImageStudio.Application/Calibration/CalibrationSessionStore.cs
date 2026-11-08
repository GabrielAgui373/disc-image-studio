using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Application.Calibration;

/// <summary>
/// Reads and writes <c>.calibration.json</c> sessions. The file format is exactly the WPF
/// wizard's (<see cref="CalibrationSessionJson"/>), so sessions move both ways; its version is
/// the target's <c>schemaVersion</c>, which is checked first so an older or newer file gets a
/// clear "unsupported version" instead of a parse error. Failures are
/// <see cref="CalibrationException"/> with a stable code.
/// </summary>
public static class CalibrationSessionStore
{
    public const string Extension = ".calibration.json";

    /// <summary>Same cap as the WPF reader.</summary>
    public const long MaximumFileBytes = 16_000_000;

    public static CalibrationSession Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                throw new CalibrationException("calibration.session.notFound", "The session file does not exist.");
            }

            if (info.Length > MaximumFileBytes)
            {
                throw new CalibrationException("calibration.session.tooLarge", "The session file is too large.");
            }

            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationException("calibration.session.unreadable", exception.Message, exception);
        }

        return Parse(json);
    }

    public static CalibrationSession Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumFileBytes)
        {
            throw new CalibrationException("calibration.session.tooLarge", "The session file is too large.");
        }

        int? version = SchemaVersion(json);
        if (version is null)
        {
            throw new CalibrationException("calibration.session.corrupt", "The file is not a calibration session.");
        }

        if (version != CalibrationTarget.CurrentSchemaVersion)
        {
            throw new CalibrationException(
                "calibration.session.unsupportedVersion",
                FormattableString.Invariant($"Session version {version} is not supported (expected {CalibrationTarget.CurrentSchemaVersion})."));
        }

        try
        {
            return CalibrationSessionJson.Deserialize(json);
        }
        catch (JsonException exception)
        {
            throw new CalibrationException("calibration.session.corrupt", exception.Message, exception);
        }
        catch (ArgumentException exception)
        {
            // The session parsed but breaks a rule (altered cells, invalid contours, ...).
            throw new CalibrationException("calibration.session.invalid", exception.Message, exception);
        }
    }

    /// <summary>Validates, serializes and replaces <paramref name="path"/> atomically.</summary>
    public static void Save(string path, CalibrationSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json;
        try
        {
            json = CalibrationSessionJson.Serialize(session);
        }
        catch (ArgumentException exception)
        {
            throw new CalibrationException("calibration.session.invalid", exception.Message, exception);
        }

        WriteAtomically(path, Encoding.UTF8.GetBytes(json), privateFile: false);
    }

    /// <summary>The companion session path of a generated target (<c>disc.iso</c> → <c>disc.calibration.json</c>).</summary>
    public static string PathBeside(string artifactPath) => Path.ChangeExtension(artifactPath, Extension);

    internal static int? SchemaVersion(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("target", out JsonElement target)
                   && target.ValueKind == JsonValueKind.Object
                   && target.TryGetProperty("schemaVersion", out JsonElement version)
                   && version.TryGetInt32(out int value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes beside the destination under a short hidden name (long destination names stay
    /// within the file-name limit), flushes, then renames over the destination.
    /// </summary>
    internal static void WriteAtomically(string path, byte[] content, bool privateFile)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
                           ?? throw new ArgumentException("The destination must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".dis-{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (privateFile && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

/// <summary>A recoverable draft: what it was, where it belongs, and when it was last written.</summary>
public sealed record CalibrationDraftInfo(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("kind")] CalibrationDiscKind Kind,
    [property: JsonPropertyName("sessionPath")] string? SessionPath,
    [property: JsonPropertyName("projectPath")] string? ProjectPath,
    [property: JsonPropertyName("savedUtc")] DateTimeOffset SavedUtc,
    [property: JsonPropertyName("hasPhoto")] bool HasPhoto,
    [property: JsonPropertyName("rectificationConfirmed")] bool RectificationConfirmed,
    [property: JsonPropertyName("tracePointCount")] int TracePointCount,
    [property: JsonPropertyName("sessionSha256")] string SessionSha256);

/// <summary>
/// Crash recovery for the wizard. While a calibration is edited the page writes a draft here
/// (a normal session file plus a small record of where it belongs); the draft is discarded once
/// the session is saved or the user abandons it. A draft left behind by a crash or a forced
/// quit is offered again on the next visit. Drafts hold the user's photo, so the folder and
/// files are private to the user.
/// </summary>
public sealed class CalibrationRecoveryStore
{
    private const string DraftSuffix = ".draft.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly IClock _clock;

    public CalibrationRecoveryStore(string directory, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The store under the application's state folder.</summary>
    public static CalibrationRecoveryStore Under(IAppPaths paths, IClock clock)
        => new(Path.Combine(paths.StateDirectory, "calibration-recovery"), clock);

    public string Directory => _directory;

    /// <summary>Writes (or replaces) the draft of <paramref name="session"/>'s target.</summary>
    public CalibrationDraftInfo Save(CalibrationSession session, string? sessionPath, string? projectPath)
    {
        ArgumentNullException.ThrowIfNull(session);
        string json;
        try
        {
            json = CalibrationSessionJson.Serialize(session);
        }
        catch (ArgumentException exception)
        {
            throw new CalibrationException("calibration.session.invalid", exception.Message, exception);
        }

        EnsureDirectory();
        string key = session.Target.Id;
        byte[] content = Encoding.UTF8.GetBytes(json);
        CalibrationDraftInfo info = new(
            key,
            session.Target.Id,
            session.Target.Parameters.Kind,
            sessionPath,
            projectPath,
            _clock.UtcNow,
            session.Photo is not null,
            session.Photo?.RectificationConfirmed == true,
            session.Photo?.Traces.Sum(trace => trace.Points.Count) ?? 0,
            Convert.ToHexStringLower(SHA256.HashData(content)));

        // The session first, the record second: a record always points at a complete session,
        // and its hash catches a session replaced by a later, interrupted write.
        CalibrationSessionStore.WriteAtomically(SessionPath(key), content, privateFile: true);
        CalibrationSessionStore.WriteAtomically(
            DraftPath(key),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, JsonOptions)),
            privateFile: true);
        return info;
    }

    /// <summary>Drafts that can be restored, newest first. Unreadable records are skipped.</summary>
    public IReadOnlyList<CalibrationDraftInfo> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        List<CalibrationDraftInfo> drafts = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*" + DraftSuffix))
        {
            if (TryReadInfo(path) is { } info && File.Exists(SessionPath(info.Key)))
            {
                drafts.Add(info);
            }
        }

        return drafts.OrderByDescending(draft => draft.SavedUtc).ToArray();
    }

    /// <summary>Loads a draft's session, checking it is the one its record describes.</summary>
    public CalibrationSession Load(string key)
    {
        string draftPath = DraftPath(CheckKey(key));
        CalibrationDraftInfo info = TryReadInfo(draftPath)
                                    ?? throw new CalibrationException("calibration.recovery.missing", "The draft no longer exists.");
        byte[] content;
        try
        {
            content = File.ReadAllBytes(SessionPath(key));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationException("calibration.recovery.missing", exception.Message, exception);
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), info.SessionSha256, StringComparison.Ordinal))
        {
            throw new CalibrationException("calibration.recovery.mismatch", "The draft session does not match its record.");
        }

        return CalibrationSessionStore.Parse(Encoding.UTF8.GetString(content));
    }

    /// <summary>Removes a draft; missing files are not an error.</summary>
    public void Discard(string key)
    {
        CheckKey(key);
        TryDelete(DraftPath(key));
        TryDelete(SessionPath(key));
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static CalibrationDraftInfo? TryReadInfo(string path)
    {
        try
        {
            CalibrationDraftInfo? info = JsonSerializer.Deserialize<CalibrationDraftInfo>(File.ReadAllText(path), JsonOptions);
            return info is not null && IsKey(info.Key) && Path.GetFileName(path) == info.Key + DraftSuffix ? info : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private string SessionPath(string key) => Path.Combine(_directory, key + CalibrationSessionStore.Extension);

    private string DraftPath(string key) => Path.Combine(_directory, key + DraftSuffix);

    // Keys are target ids (32 hex digits); anything else could escape the folder.
    private static bool IsKey(string? key) => key is { Length: 32 } && Guid.TryParseExact(key, "N", out _);

    private static string CheckKey(string key)
        => IsKey(key) ? key : throw new ArgumentException("A draft key is a target id.", nameof(key));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
