using System.Text.Json;
using System.Text.Json.Serialization;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.Application.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;
    private readonly IAtomicFileStorage _storage;
    private readonly IAppLogger _logger;

    public JsonSettingsStore(
        string filePath,
        IAtomicFileStorage? storage = null,
        IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _storage = storage ?? new PhysicalAtomicFileStorage();
        _logger = logger ?? new NullAppLogger();
    }

    public UserSettings Load()
    {
        if (!_storage.Exists(_filePath))
        {
            return new UserSettings();
        }

        try
        {
            using Stream stream = _storage.OpenRead(_filePath);
            UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(stream, JsonOptions);
            if (settings is null || settings.SchemaVersion > UserSettings.CurrentSchemaVersion)
            {
                return new UserSettings();
            }

            return settings.Normalize();
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or NotSupportedException
                                              or ArgumentException)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "settings.load.recovered",
                "Settings could not be read; English defaults were loaded.",
                exception);
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _storage.WriteAtomically(
            _filePath,
            stream => JsonSerializer.Serialize(stream, settings.Normalize(), JsonOptions));
    }
}
