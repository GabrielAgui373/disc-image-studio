using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Ports;

/// <summary>Thread-safe clock port. Implementations must return UTC-aware timestamps.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Small filesystem seam used for path normalization and capability checks.</summary>
public interface IFileSystem
{
    string GetFullPath(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    void DeleteFile(string path);
}

public readonly record struct ScannedEntry(string Path, bool IsDirectory);

/// <summary>
/// Read-only, bounded directory enumeration used to search for relocated assets. Implementations
/// must skip symbolic links and inaccessible entries instead of throwing.
/// </summary>
public interface IDirectoryScanner
{
    IEnumerable<ScannedEntry> Enumerate(string root, int maximumDepth);
}

/// <summary>Platform-owned locations; no environment lookup is performed by use cases.</summary>
public interface IApplicationPaths
{
    string ProjectsDirectory { get; }

    string SettingsDirectory { get; }

    string CacheDirectory { get; }
}

public interface IProjectDocumentStore
{
    ProjectDocument Load(string path);

    void Save(string path, ProjectDocument document);
}

public enum UnsavedChangesDecision
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// UI adapter called on the caller's synchronization context. It returns a semantic decision,
/// never a framework dialog result or localized button text.
/// </summary>
public interface IUserInteraction
{
    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
        UnsavedChangesRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record UnsavedChangesRequest(string ProjectName, string Context);

/// <summary>
/// Framework-neutral event sink. Implementations choose dispatching; application services
/// publish synchronously and never assume a UI thread.
/// </summary>
public interface IApplicationEventPublisher
{
    void Publish<TEvent>(TEvent applicationEvent) where TEvent : notnull;
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void DeleteFile(string path) => File.Delete(path);
}

public sealed class PhysicalDirectoryScanner : IDirectoryScanner
{
    public IEnumerable<ScannedEntry> Enumerate(string root, int maximumDepth)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = maximumDepth,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            // Symbolic links are skipped so a search can never escape the chosen folder.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        DirectoryInfo directory = new(root);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", options))
        {
            yield return new ScannedEntry(entry.FullName, entry is DirectoryInfo);
        }
    }
}

public sealed class JsonProjectDocumentStore : IProjectDocumentStore
{
    public ProjectDocument Load(string path) => ProjectJsonStore.Load(path);

    public void Save(string path, ProjectDocument document) => ProjectJsonStore.Save(path, document);
}

public sealed class NullApplicationEventPublisher : IApplicationEventPublisher
{
    public void Publish<TEvent>(TEvent applicationEvent) where TEvent : notnull
    {
    }
}
