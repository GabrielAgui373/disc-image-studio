using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.Application.Projects;

public enum ProjectAssetKind
{
    LayerImage,
    DataDirectory,
    CalibrationSession,
}

/// <summary>
/// One external file or folder a project refers to. <see cref="Key"/> identifies the reference
/// inside the project (a layer id, or the fixed key of a delivery/identity field).
/// <see cref="DisplayName"/> is user data (a layer name) or that fixed key; hosts localize
/// non-layer references from <see cref="Kind"/>.
/// </summary>
public sealed record ProjectAssetReference(
    ProjectAssetKind Kind,
    string Key,
    string DisplayName,
    string StoredPath,
    string ResolvedPath,
    bool Exists)
{
    public const string DataDirectoryKey = "delivery.dataDirectory";
    public const string CalibrationSessionKey = "identity.calibrationSession";

    public bool IsDirectory => Kind == ProjectAssetKind.DataDirectory;

    public string EntryName => Path.GetFileName(ResolvedPath);
}

public enum RelinkMatch
{
    /// <summary>Exactly one entry with the same name was found.</summary>
    Exact,

    /// <summary>One entry matches only when letter case is ignored; it may be a different file.</summary>
    CaseDiffers,

    /// <summary>Several entries share the name; the user must pick one individually.</summary>
    Ambiguous,

    NotFound,
}

public sealed record RelinkProposal(
    ProjectAssetReference Asset,
    RelinkMatch Match,
    string? CandidatePath,
    int CandidateCount);

public sealed record RelinkSearchResult(
    IReadOnlyList<RelinkProposal> Proposals,
    bool ReachedScanLimit);

/// <summary>
/// Finds the external assets of a project, reports which are missing and produces relink
/// proposals. Every method is side-effect free on disk: it never copies, moves or deletes a
/// user file, and a relink only returns an updated workspace for the caller to apply.
/// </summary>
public sealed class ProjectAssetService
{
    public const int MaximumScanDepth = 8;
    public const int MaximumScannedEntries = 50_000;

    private readonly IFileSystem _fileSystem;
    private readonly IDirectoryScanner _scanner;

    public ProjectAssetService(IFileSystem fileSystem, IDirectoryScanner scanner)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public IReadOnlyList<ProjectAssetReference> Inspect(ProjectWorkspace workspace, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        List<ProjectAssetReference> assets = [];
        foreach (DesignLayerState layer in workspace.Design.Layers)
        {
            assets.Add(Reference(ProjectAssetKind.LayerImage, layer.Id, layer.Name, layer.ImagePath, projectFilePath));
        }

        if (!string.IsNullOrWhiteSpace(workspace.Delivery.DataDirectory))
        {
            assets.Add(Reference(
                ProjectAssetKind.DataDirectory,
                ProjectAssetReference.DataDirectoryKey,
                ProjectAssetReference.DataDirectoryKey,
                workspace.Delivery.DataDirectory,
                projectFilePath));
        }

        if (!string.IsNullOrWhiteSpace(workspace.Identity.CalibrationSessionPath))
        {
            assets.Add(Reference(
                ProjectAssetKind.CalibrationSession,
                ProjectAssetReference.CalibrationSessionKey,
                ProjectAssetReference.CalibrationSessionKey,
                workspace.Identity.CalibrationSessionPath,
                projectFilePath));
        }

        return assets;
    }

    /// <summary>
    /// Returns a copy of <paramref name="workspace"/> whose reference points at
    /// <paramref name="newPath"/>. The new target must exist and be of the same kind (file or
    /// folder); it is stored as an absolute path so the WPF host can still open the project.
    /// </summary>
    public ProjectWorkspace Relink(ProjectWorkspace workspace, ProjectAssetReference asset, string newPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(asset);
        string target = ProjectPaths.TryNormalize(newPath)
            ?? throw new ArgumentException("The replacement path is not valid.", nameof(newPath));
        bool exists = asset.IsDirectory ? _fileSystem.DirectoryExists(target) : _fileSystem.FileExists(target);
        if (!exists)
        {
            throw new ArgumentException("The replacement must be an existing item of the same kind.", nameof(newPath));
        }

        return asset.Kind switch
        {
            ProjectAssetKind.LayerImage => workspace with
            {
                Design = new DesignState(workspace.Design.Layers.Select(layer => layer.Id == asset.Key
                    ? new DesignLayerState(
                        layer.Id,
                        layer.Name,
                        target,
                        layer.Visible,
                        layer.TranslateXMm,
                        layer.TranslateYMm,
                        layer.RotationDeg,
                        layer.Scale)
                    : layer)),
            },
            ProjectAssetKind.DataDirectory => workspace with
            {
                Delivery = new DeliveryState(
                    workspace.Delivery.OutputPath,
                    target,
                    workspace.Delivery.VolumeLabel,
                    workspace.Delivery.FilesAtOuterRing),
            },
            ProjectAssetKind.CalibrationSession => workspace with
            {
                Identity = new ProjectIdentity(
                    workspace.Identity.Name,
                    workspace.Identity.CreatedUtc,
                    workspace.Identity.ModifiedUtc,
                    target,
                    workspace.Identity.ThumbnailPngBase64),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(asset)),
        };
    }

    /// <summary>
    /// Searches <paramref name="folder"/> for entries named like each missing asset. The scan is
    /// bounded in depth and entry count and does not follow symbolic links, so it cannot leave
    /// the chosen folder or stall on a large tree. Results are proposals only.
    /// </summary>
    public RelinkSearchResult FindInFolder(
        IEnumerable<ProjectAssetReference> missingAssets,
        string folder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(missingAssets);
        ProjectAssetReference[] wanted = missingAssets.Where(asset => !asset.Exists).ToArray();
        string root = ProjectPaths.TryNormalize(folder)
            ?? throw new ArgumentException("The search folder is not valid.", nameof(folder));
        if (wanted.Length == 0 || !_fileSystem.DirectoryExists(root))
        {
            return new RelinkSearchResult(wanted.Select(NotFound).ToArray(), false);
        }

        HashSet<string> wantedNames = new(wanted.Select(asset => asset.EntryName), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<ScannedEntry>> found = new(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        bool reachedLimit = false;
        foreach (ScannedEntry entry in _scanner.Enumerate(root, MaximumScanDepth))
        {
            if (++scanned % 256 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (scanned > MaximumScannedEntries)
            {
                reachedLimit = true;
                break;
            }

            string name = Path.GetFileName(entry.Path);
            if (wantedNames.Contains(name))
            {
                if (!found.TryGetValue(name, out List<ScannedEntry>? matches))
                {
                    found[name] = matches = [];
                }

                matches.Add(entry);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RelinkSearchResult(
            wanted.Select(asset => Propose(asset, found.GetValueOrDefault(asset.EntryName) ?? [])).ToArray(),
            reachedLimit);
    }

    private static RelinkProposal Propose(ProjectAssetReference asset, List<ScannedEntry> sameNameIgnoringCase)
    {
        ScannedEntry[] sameKind = sameNameIgnoringCase.Where(entry => entry.IsDirectory == asset.IsDirectory).ToArray();
        ScannedEntry[] exact = sameKind
            .Where(entry => string.Equals(Path.GetFileName(entry.Path), asset.EntryName, StringComparison.Ordinal))
            .ToArray();
        return exact.Length switch
        {
            1 => new RelinkProposal(asset, RelinkMatch.Exact, exact[0].Path, 1),
            > 1 => new RelinkProposal(asset, RelinkMatch.Ambiguous, null, exact.Length),
            _ => sameKind.Length switch
            {
                0 => NotFound(asset),
                1 => new RelinkProposal(asset, RelinkMatch.CaseDiffers, sameKind[0].Path, 1),
                _ => new RelinkProposal(asset, RelinkMatch.Ambiguous, null, sameKind.Length),
            },
        };
    }

    private static RelinkProposal NotFound(ProjectAssetReference asset) => new(asset, RelinkMatch.NotFound, null, 0);

    private ProjectAssetReference Reference(
        ProjectAssetKind kind,
        string key,
        string displayName,
        string storedPath,
        string? projectFilePath)
    {
        string resolved = ProjectPaths.ResolveAsset(projectFilePath, storedPath) ?? storedPath;
        bool exists = kind == ProjectAssetKind.DataDirectory
            ? _fileSystem.DirectoryExists(resolved)
            : _fileSystem.FileExists(resolved);
        return new ProjectAssetReference(kind, key, displayName, storedPath, resolved, exists);
    }
}
