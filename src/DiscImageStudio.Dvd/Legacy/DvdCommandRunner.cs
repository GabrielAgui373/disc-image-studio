namespace DvdImageSolver;

/// <summary>
/// Stable facade shared by the module catalog and the historical command-line host.
/// Parsing stays at this adapter boundary; the engine remains independent of text commands.
/// </summary>
public static class DvdCommandRunner
{
    public static int Run(params string[] arguments) => DvdCommandApplication.RunCommand(arguments);
}
