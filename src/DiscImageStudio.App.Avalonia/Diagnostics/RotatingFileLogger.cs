using System.Text;
using System.Text.RegularExpressions;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Diagnostics;

public sealed partial class RotatingFileLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _homeDirectory;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public RotatingFileLogger(
        string logsDirectory,
        string? homeDirectory = null,
        long maximumBytes = 2 * 1024 * 1024,
        int retainedFiles = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (retainedFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        }

        _filePath = Path.Combine(logsDirectory, "disc-image-studio.log");
        _homeDirectory = homeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
    }

    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        Exception? exception = null,
        Guid? correlationId = null)
    {
        string id = (correlationId ?? Guid.NewGuid()).ToString("N");
        StringBuilder line = new()
        {
            Capacity = 512,
        };
        line.Append(DateTimeOffset.UtcNow.ToString("O"));
        line.Append(" level=").Append(level);
        line.Append(" event=").Append(Redact(eventName));
        line.Append(" correlationId=").Append(id);
        line.Append(" message=\"").Append(Redact(message).Replace("\"", "'", StringComparison.Ordinal)).Append('"');
        if (exception is not null)
        {
            line.AppendLine();
            line.Append(Redact(exception.ToString()));
        }

        lock (_gate)
        {
            try
            {
                string directory = Path.GetDirectoryName(_filePath)!;
                Directory.CreateDirectory(directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line.ToString()) + Environment.NewLine.Length);
                File.AppendAllText(_filePath, line.AppendLine().ToString(), Encoding.UTF8);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[{level}] {eventName} ({id}): {Redact(message)}");
            }
        }
    }

    private string Redact(string value)
    {
        string redacted = string.IsNullOrWhiteSpace(_homeDirectory)
            ? value
            : value.Replace(_homeDirectory, "$HOME", StringComparison.Ordinal);
        return SecretPattern().Replace(redacted, "$1=[REDACTED]");
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length + incomingBytes <= _maximumBytes)
        {
            return;
        }

        string oldest = $"{_filePath}.{_retainedFiles}";
        File.Delete(oldest);
        for (int index = _retainedFiles - 1; index >= 1; index--)
        {
            string source = $"{_filePath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_filePath}.{index + 1}", overwrite: true);
            }
        }

        File.Move(_filePath, $"{_filePath}.1", overwrite: true);
    }

    [GeneratedRegex("(?i)\\b(token|password|secret|api[_-]?key)\\s*=\\s*[^\\s;]+")]
    private static partial Regex SecretPattern();
}
