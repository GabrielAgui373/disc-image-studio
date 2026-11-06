using DiscImageStudio.Application.Results;

namespace DiscImageStudio.Application.Navigation;

public enum WorkspacePage
{
    Home,
    Design,
    Preview,
    Export,
    Burn,
    Calibration,
    Settings,
}

public sealed class NavigationState
{
    public WorkspacePage Current { get; private set; } = WorkspacePage.Home;

    public ApplicationResult NavigateTo(
        WorkspacePage destination,
        bool hasOpenProject,
        bool allowProjectlessNavigation = false)
    {
        if (RequiresProject(destination) && !hasOpenProject && !allowProjectlessNavigation)
        {
            return ApplicationResult.Failure(
                ApplicationErrorCode.NavigationRequiresProject,
                destination.ToString());
        }

        Current = destination;
        return ApplicationResult.Success();
    }

    internal void ProjectOpened() => Current = WorkspacePage.Design;

    internal void ProjectClosed() => Current = WorkspacePage.Home;

    private static bool RequiresProject(WorkspacePage destination)
        => destination is not (WorkspacePage.Home or WorkspacePage.Settings);
}
