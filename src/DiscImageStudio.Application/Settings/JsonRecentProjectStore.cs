using System.Text.Json;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Settings;

public sealed class JsonRecentProjectStore : IRecentProjectStore
{
    public const int MaximumEntries = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly IAtomicFileStorage _storage;
    private readonly IAppLogger _logger;

    public JsonRecentProjectStore(
        string filePath,
        IAtomicFileStorage? storage = null,
        IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _storage = storage ?? new PhysicalAtomicFileStorage();
        _logger = logger ?? new NullAppLogger();
    }

    public IReadOnlyList<RecentProjectEntry> Load()
    {
        if (!_storage.Exists(_filePath))
        {
            return [];
        }

        try
        {
            using Stream stream = _storage.OpenRead(_filePath);
            return Sanitize(JsonSerializer.Deserialize<List<RecentProjectEntry>>(stream, JsonOptions) ?? []);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or NotSupportedException)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "recent-projects.load.recovered",
                "Recent projects could not be read; an empty history was loaded.",
                exception);
            return [];
        }
    }

    public void Record(RecentProjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<RecentProjectEntry> entries = [entry];
        entries.AddRange(Load().Where(existing => !SamePath(existing.ProjectPath, entry.ProjectPath)));
        Save(Sanitize(entries));
    }

    public void Remove(string projectPath)
        => Save(Load().Where(entry => !SamePath(entry.ProjectPath, projectPath)).ToArray());

    public void Clear() => Save([]);

    private void Save(IReadOnlyList<RecentProjectEntry> entries)
    {
        try
        {
            _storage.WriteAtomically(
                _filePath,
                stream => JsonSerializer.Serialize(stream, entries.Take(MaximumEntries), JsonOptions));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "recent-projects.save.failed",
                "Recent project history could not be saved.",
                exception);
        }
    }

    private static IReadOnlyList<RecentProjectEntry> Sanitize(IEnumerable<RecentProjectEntry> entries)
    {
        List<RecentProjectEntry> result = [];
        foreach (RecentProjectEntry entry in entries.OrderByDescending(entry => entry.LastOpenedUtc))
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.ProjectPath)
                || !ProjectJsonStore.IsKnownFamily(entry.DiscFamily)
                || result.Any(existing => SamePath(existing.ProjectPath, entry.ProjectPath)))
            {
                continue;
            }

            result.Add(entry);
            if (result.Count == MaximumEntries)
            {
                break;
            }
        }

        return result;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
