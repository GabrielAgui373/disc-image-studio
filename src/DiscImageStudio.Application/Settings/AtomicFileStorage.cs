namespace DiscImageStudio.Application.Settings;

public interface IAtomicFileStorage
{
    bool Exists(string path);

    Stream OpenRead(string path);

    void WriteAtomically(string path, Action<Stream> write);
}

public sealed class PhysicalAtomicFileStorage : IAtomicFileStorage
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public bool Exists(string path) => File.Exists(path);

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public void WriteAtomically(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, fullPath, overwrite: true);
            SetPrivateFileMode(fullPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

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
