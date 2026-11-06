using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Platform;

public sealed class FileCacheManager(IAppPaths paths, IAppLogger logger) : ICacheManager
{
    public long GetSizeBytes()
    {
        try
        {
            return Directory.Exists(paths.CacheDirectory)
                ? Directory.EnumerateFiles(paths.CacheDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.Log(AppLogLevel.Warning, "cache.size.failed", "Cache size could not be calculated.", exception);
            return 0;
        }
    }

    public void Clear()
    {
        if (!Directory.Exists(paths.CacheDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(paths.CacheDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.Log(AppLogLevel.Warning, "cache.delete.failed", "A cache file could not be deleted.", exception);
            }
        }
    }
}
