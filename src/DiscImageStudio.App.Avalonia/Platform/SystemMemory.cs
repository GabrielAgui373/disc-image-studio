namespace DiscImageStudio.App.Avalonia.Platform;

/// <summary>
/// Memory a new allocation can count on. On Linux this is <c>MemAvailable</c> from
/// <c>/proc/meminfo</c> (free memory plus what the kernel can reclaim); elsewhere, or if that
/// cannot be read, half of what .NET reports as available to the process.
/// </summary>
public static class SystemMemory
{
    public static long AvailableBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    {
                        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out long kibibytes))
                        {
                            return kibibytes * 1024;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the runtime's view.
        }

        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2;
    }
}
