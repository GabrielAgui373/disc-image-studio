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
        ["navigation.home"] = "Home",
        ["navigation.design"] = "Design",
        ["navigation.preview"] = "Preview",
        ["navigation.export"] = "Export",
        ["navigation.burn"] = "Burn",
        ["navigation.settings"] = "Settings",
        ["navigation.requiresProject"] = "Open or create a project before continuing. You were returned to Home.",
        ["navigation.placeholderRequiresProject"] = "This workspace is a preview. Open or create a project before its workflow actions become available.",
        ["home.placeholder.title"] = "Welcome to Disc Image Studio",
        ["home.placeholder.description"] = "Create or open a project to begin the disc-image workflow.",
        ["settings.placeholder.title"] = "Settings",
        ["settings.placeholder.description"] = "Application preferences will be available here in a later workflow task.",
        ["workspace.placeholder.title"] = "Workspace stage",
        ["workspace.placeholder.description"] = "This stage will be available when its workflow is implemented.",
        ["job.started"] = "Running",
        ["job.succeeded"] = "Completed",
        ["job.canceled"] = "Canceled",
        ["job.failed"] = "Failed",
        ["jobDock.title"] = "Jobs",
        ["jobDock.empty"] = "No job activity yet.",
        ["jobDock.cancel"] = "Cancel",
        ["jobDock.clear"] = "Clear completed",
        ["jobDock.details"] = "Job details",
        ["toast.jobStarted"] = "Job started",
        ["toast.jobCompleted"] = "Job completed",
        ["toast.jobCanceled"] = "Job canceled",
        ["toast.jobFailed"] = "Job failed",
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
