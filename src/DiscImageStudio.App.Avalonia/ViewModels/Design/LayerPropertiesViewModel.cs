using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.ViewModels.Fields;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Projects;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>
/// Property fields of the selected layer. Each field commits one history step on Enter or focus
/// loss (see <see cref="NumericFieldViewModel"/>); the panel follows canvas drags and undo live.
/// </summary>
public sealed partial class LayerPropertiesViewModel : ObservableObject
{
    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private string? _layerId;

    public LayerPropertiesViewModel(ProjectLifecycleService projects, ProjectEditor editor, ITextCatalog text)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        string range = text["field.range"];
        string rejected = text["field.rejected"];
        X = new NumericFieldViewModel(text["design.props.x"], "mm", -120, 120, 2,
            value => ApplyTransform(transform => transform with { TranslateXMm = value }), range, rejected);
        Y = new NumericFieldViewModel(text["design.props.y"], "mm", -120, 120, 2,
            value => ApplyTransform(transform => transform with { TranslateYMm = value }), range, rejected);
        Scale = new NumericFieldViewModel(text["design.props.scale"], "%", 1, 2000, 1,
            value => ApplyTransform(transform => transform with { Scale = value / 100 }), range, rejected);
        Rotation = new NumericFieldViewModel(text["design.props.rotation"], "°", -360, 360, 1,
            value => ApplyTransform(transform => transform with { RotationDeg = value }), range, rejected);
        Opacity = new NumericFieldViewModel(text["design.props.opacity"], "%", 0, 100, 0,
            value => Apply(HistoryLabels.LayerOpacity, (workspace, id) => DesignEdits.SetOpacity(workspace, id, value / 100)), range, rejected);
        Title = text["design.props.title"];
        NameLabel = text["design.props.name"];
        ResetLabel = text["design.props.reset"];
        NoSelection = text["design.props.none"];
        ImageLabel = text["design.props.image"];
    }

    public string Title { get; }

    public string NameLabel { get; }

    public string ResetLabel { get; }

    public string NoSelection { get; }

    public string ImageLabel { get; }

    public NumericFieldViewModel X { get; }

    public NumericFieldViewModel Y { get; }

    public NumericFieldViewModel Scale { get; }

    public NumericFieldViewModel Rotation { get; }

    public NumericFieldViewModel Opacity { get; }

    [ObservableProperty]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    /// <summary>Set by the view while the name box has focus, so live updates do not overwrite typing.</summary>
    public bool IsEditingName { get; set; }

    [ObservableProperty]
    public partial string ImagePath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    public void Sync(string? layerId)
    {
        ProjectWorkspace? workspace = _projects.Current;
        DesignLayerState? layer = workspace?.Design.Layers.FirstOrDefault(candidate => candidate.Id == layerId);
        _layerId = layer?.Id;
        HasSelection = layer is not null;
        if (layer is null)
        {
            return;
        }

        if (!IsEditingName)
        {
            NameText = layer.Name;
        }

        X.Show(layer.TranslateXMm);
        Y.Show(layer.TranslateYMm);
        Scale.Show(layer.Scale * 100);
        Rotation.Show(layer.RotationDeg);
        Opacity.Show(Math.Round(layer.Opacity * 100, MidpointRounding.AwayFromZero));
        ImagePath = layer.ImagePath;
    }

    public void SetMissing(bool missing) => IsMissing = missing;

    [RelayCommand]
    private void CommitName()
    {
        string name = NameText.Trim();
        if (name.Length == 0 || _layerId is null)
        {
            Sync(_layerId);
            return;
        }

        Apply(HistoryLabels.RenameLayer, (workspace, id) => DesignEdits.RenameLayer(workspace, id, name));
    }

    [RelayCommand]
    private void RevertName()
    {
        IsEditingName = false;
        Sync(_layerId);
    }

    [RelayCommand]
    private void ResetTransform()
        => ApplyTransform(_ => new LayerTransform(0, 0, 0, 1));

    private bool ApplyTransform(Func<LayerTransform, LayerTransform> change)
        => Apply(HistoryLabels.TransformLayer, (workspace, id) =>
        {
            DesignLayerState layer = workspace.Design.Layers[DesignEdits.IndexOf(workspace.Design.Layers, id)];
            return DesignEdits.SetTransform(workspace, id, change(LayerTransform.Of(layer)));
        });

    private bool Apply(string label, Func<ProjectWorkspace, string, ProjectWorkspace> edit)
    {
        string? id = _layerId;
        if (id is null || !_editor.CanApply)
        {
            return false;
        }

        return _editor.Apply(label, workspace => edit(workspace, id), [id]).Succeeded;
    }
}
