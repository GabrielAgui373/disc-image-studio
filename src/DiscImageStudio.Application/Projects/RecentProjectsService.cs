using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.Application.Projects;

public enum RecentProjectStatus
{
    Available,
    Missing,
    AccessDenied,
    UnsupportedSchema,
    Invalid,
}

/// <summary>
/// The current state of one recent project file, read without opening the project. Name and
/// family come from the file when it is readable, otherwise from the history entry.
/// </summary>
public sealed record RecentProjectProbe(
    RecentProjectEntry Entry,
    RecentProjectStatus Status,
    string Name,
    OpticalDiscFamily Family,
    byte[]? ThumbnailPng);

/// <summary>
/// Reads the recent-project history and probes each file for the Home page. Probing performs
/// blocking file I/O and is meant to run off the UI thread. Removing an entry only edits the
/// history; the project file on disk is never touched.
/// </summary>
public sealed class RecentProjectsService
{
    /// <summary>Upper bound for a decoded thumbnail; larger payloads fall back to artwork.</summary>
    public const int MaximumThumbnailBytes = 384 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IRecentProjectStore _store;
    private readonly IProjectDocumentStore _documents;
    private readonly IFileSystem _fileSystem;

    public RecentProjectsService(
        IRecentProjectStore store,
        IProjectDocumentStore documents,
        IFileSystem fileSystem)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public IReadOnlyList<RecentProjectEntry> Load() => _store.Load();

    public void Remove(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        _store.Remove(projectPath);
    }

    public RecentProjectProbe Probe(RecentProjectEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        OpticalDiscFamily entryFamily = ToFamily(entry.DiscFamily);
        byte[]? entryThumbnail = DecodeThumbnail(entry.ThumbnailPngBase64);
        if (!_fileSystem.FileExists(entry.ProjectPath))
        {
            return new RecentProjectProbe(entry, RecentProjectStatus.Missing, entry.Name, entryFamily, entryThumbnail);
        }

        try
        {
            ProjectDocument document = _documents.Load(entry.ProjectPath);
            return new RecentProjectProbe(
                entry,
                RecentProjectStatus.Available,
                document.Name,
                document.Family,
                DecodeThumbnail(document.ThumbnailPngBase64) ?? entryThumbnail);
        }
        catch (UnauthorizedAccessException)
        {
            return new RecentProjectProbe(entry, RecentProjectStatus.AccessDenied, entry.Name, entryFamily, entryThumbnail);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RecentProjectProbe(entry, RecentProjectStatus.Missing, entry.Name, entryFamily, entryThumbnail);
        }
        catch (InvalidDataException exception) when (ProjectJsonStore.IsUnsupportedSchema(exception))
        {
            return new RecentProjectProbe(entry, RecentProjectStatus.UnsupportedSchema, entry.Name, entryFamily, entryThumbnail);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new RecentProjectProbe(entry, RecentProjectStatus.Invalid, entry.Name, entryFamily, entryThumbnail);
        }
    }

    public static OpticalDiscFamily ToFamily(string? familyToken)
        => familyToken switch
        {
            ProjectDocument.DvdFamily => OpticalDiscFamily.Dvd,
            ProjectDocument.BluRayFamily => OpticalDiscFamily.BluRay,
            _ => OpticalDiscFamily.CompactDisc,
        };

    public static string ToFamilyToken(OpticalDiscFamily family)
        => family switch
        {
            OpticalDiscFamily.Dvd => ProjectDocument.DvdFamily,
            OpticalDiscFamily.BluRay => ProjectDocument.BluRayFamily,
            _ => ProjectDocument.CdFamily,
        };

    /// <summary>
    /// Decodes a stored thumbnail only when it is a bounded PNG payload; anything else is
    /// treated as absent so a corrupt or oversized history entry cannot stall the Home page.
    /// </summary>
    public static byte[]? DecodeThumbnail(string? pngBase64)
    {
        if (string.IsNullOrWhiteSpace(pngBase64)
            || pngBase64.Length > ((MaximumThumbnailBytes + 2) / 3 * 4))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(pngBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        return bytes.Length is > 0 and <= MaximumThumbnailBytes && bytes.AsSpan().StartsWith(PngSignature)
            ? bytes
            : null;
    }
}
