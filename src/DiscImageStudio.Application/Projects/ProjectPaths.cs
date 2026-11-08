namespace DiscImageStudio.Application.Projects;

/// <summary>
/// Path identity and resolution rules shared by recent projects and asset references.
/// Linux file systems are case-sensitive, so two paths that differ only in case are different
/// projects there; Windows and macOS compare case-insensitively by default.
/// </summary>
public static class ProjectPaths
{
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        Comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns an absolute path without trailing separators, or <see langword="null"/> when the
    /// value cannot be a path on this platform.
    /// </summary>
    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path.Trim());
            string? root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0)
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static bool AreSame(string? left, string? right)
    {
        string? normalizedLeft = TryNormalize(left);
        string? normalizedRight = TryNormalize(right);
        return normalizedLeft is not null
               && normalizedRight is not null
               && string.Equals(normalizedLeft, normalizedRight, Comparison);
    }

    /// <summary>
    /// Resolves a stored asset path. Absolute paths (every path the WPF host writes) are used as
    /// is; a relative path is resolved against the folder that contains the project file.
    /// </summary>
    /// <summary>
    /// The project file name for <paramref name="projectName"/>: invalid file-name characters
    /// become underscores and the project extension is added. New projects and renames use it.
    /// </summary>
    public static string FileNameFor(string projectName)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        char[] invalid = Path.GetInvalidFileNameChars();
        string stem = new string(projectName.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim()
            .Trim('.');
        return $"{(string.IsNullOrWhiteSpace(stem) ? "disc-project" : stem)}{DiscImageStudio.Core.ProjectJsonStore.FileExtension}";
    }

    public static string? ResolveAsset(string? projectFilePath, string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return null;
        }

        string trimmed = storedPath.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return TryNormalize(trimmed);
        }

        string? projectDirectory = string.IsNullOrWhiteSpace(projectFilePath)
            ? null
            : Path.GetDirectoryName(TryNormalize(projectFilePath));
        return projectDirectory is null ? TryNormalize(trimmed) : TryNormalize(Path.Combine(projectDirectory, trimmed));
    }
}
