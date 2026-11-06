namespace DiscImageStudio.App.Avalonia.Localization;

public interface ITextCatalog
{
    string this[string resourceId] { get; }
}

public sealed class EnglishTextCatalog : ITextCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app.title"] = "Disc Image Studio",
        ["host.status.title"] = "Linux host ready",
        ["host.status.description"] = "Avalonia platform services are configured and ready for the application shell.",
        ["about.title"] = "Runtime information",
        ["about.version"] = "Version",
        ["about.commit"] = "Commit",
        ["about.runtime"] = ".NET runtime",
        ["about.avalonia"] = "Avalonia",
        ["about.operatingSystem"] = "Operating system",
        ["about.configDirectory"] = "Configuration",
        ["about.dataDirectory"] = "Application data",
        ["about.cacheDirectory"] = "Cache",
        ["about.logsDirectory"] = "Logs",
        ["action.copyDiagnostics"] = "Copy diagnostics",
        ["action.openLogs"] = "Open logs folder",
        ["action.diagnosticsCopied"] = "Diagnostics copied.",
        ["action.logsOpened"] = "Logs folder opened.",
        ["action.logsOpenFailed"] = "The logs folder could not be opened.",
        ["prompt.shutdown.title"] = "Stop active work?",
        ["prompt.shutdown.message"] = "The application has active work. Closing will cancel it and wait for cleanup.",
        ["prompt.unsaved.title"] = "Save project changes?",
        ["prompt.unsaved.message"] = "The project has unsaved changes.",
        ["prompt.continue"] = "Continue",
        ["prompt.save"] = "Save",
        ["prompt.discard"] = "Discard",
        ["prompt.cancel"] = "Cancel",
        ["prompt.ok"] = "OK",
        ["shutdown.timeout.title"] = "Cleanup is still running",
        ["shutdown.timeout.message"] = "The application could not finish job cleanup within 10 seconds. Try closing again after the job stops.",
    };

    public string this[string resourceId]
        => Values.TryGetValue(resourceId, out string? value)
            ? value
            : throw new KeyNotFoundException($"Unknown English resource ID '{resourceId}'.");
}
