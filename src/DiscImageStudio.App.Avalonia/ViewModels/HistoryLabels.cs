namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// History label codes passed to <c>ProjectEditor</c>. Each code has a catalog entry
/// <c>history.label.&lt;code&gt;</c> used for "Undo …"/"Redo …" tooltips and status messages.
/// </summary>
public static class HistoryLabels
{
    public const string AddLayers = "layers.add";
    public const string RemoveLayers = "layers.remove";
    public const string MoveLayer = "layer.move";
    public const string RenameLayer = "layer.rename";
    public const string DuplicateLayer = "layer.duplicate";
    public const string LayerVisibility = "layer.visibility";
    public const string TransformLayer = "layer.transform";
    public const string LayerOpacity = "layer.opacity";
    public const string RelinkLayer = "layer.relink";
    public const string Tone = "tone.set";
    public const string DataFolder = "data.set";
    public const string OutputFile = "output.set";
    public const string ProjectSettings = "project.settings";
    public const string CalibrationSession = "calibration.session";
}
