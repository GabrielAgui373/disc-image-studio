using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

public enum DesignPanel
{
    Layers,
    Tone,
    DiscFiles,
}

/// <summary>
/// The Design page: canvas, layer list, properties, tone and disc files. Every persistent change
/// goes through <see cref="ProjectEditor"/>; the panels follow the canvas (selection, drag
/// previews, undo) through its repaint notifications.
/// </summary>
public sealed partial class DesignWorkspaceViewModel : ObservableObject
{
    private static readonly FilePickerType ImageFiles = new(
        "Images",
        ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif"],
        ["image/png", "image/jpeg", "image/bmp", "image/gif"]);

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly ProjectAssetService _assets;
    private readonly IFilePickerService _filePicker;
    private readonly ITextCatalog _text;

    public DesignWorkspaceViewModel(
        DesignCanvasViewModel canvas,
        ProjectLifecycleService projects,
        ProjectEditor editor,
        ProjectAssetService assets,
        IFilePickerService filePicker,
        IExternalLauncher launcher,
        ITextCatalog text)
    {
        Canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Properties = new LayerPropertiesViewModel(projects, editor, text);
        Tone = new ToneSettingsViewModel(projects, editor, text);
        DiscFiles = new DiscFilesViewModel(projects, editor, filePicker, launcher, text);
        LayersTabLabel = text["design.tab.layers"];
        ToneTabLabel = text["design.tab.tone"];
        FilesTabLabel = text["design.tab.files"];
        ImportLabel = text["design.layers.import"];
        LayersEmpty = text["design.layers.empty"];
        MoveUpLabel = text["design.layers.moveUp"];
        MoveDownLabel = text["design.layers.moveDown"];
        DuplicateLabel = text["design.layers.duplicate"];
        RemoveLabel = text["design.layers.remove"];
        ToggleVisibilityLabel = text["design.layers.visibility"];
        RelinkLabel = text["design.layers.relink"];
        MissingLabel = text["design.canvas.missing"];
        DragLabel = text["design.layers.drag"];
        Canvas.InvalidateRequested += (_, _) => Sync();
        Layers.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLayers));
    }

    public DesignCanvasViewModel Canvas { get; }

    public LayerListViewModel Layers { get; } = new();

    public LayerPropertiesViewModel Properties { get; }

    public ToneSettingsViewModel Tone { get; }

    public DiscFilesViewModel DiscFiles { get; }

    public string LayersTabLabel { get; }

    public string ToneTabLabel { get; }

    public string FilesTabLabel { get; }

    public string ImportLabel { get; }

    public string LayersEmpty { get; }

    public string MoveUpLabel { get; }

    public string MoveDownLabel { get; }

    public string DuplicateLabel { get; }

    public string RemoveLabel { get; }

    public string ToggleVisibilityLabel { get; }

    public string RelinkLabel { get; }

    public string MissingLabel { get; }

    public string DragLabel { get; }

    public bool HasLayers => Layers.Items.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayersPanel), nameof(IsTonePanel), nameof(IsFilesPanel))]
    public partial DesignPanel ActivePanel { get; set; }

    public bool IsLayersPanel => ActivePanel == DesignPanel.Layers;

    public bool IsTonePanel => ActivePanel == DesignPanel.Tone;

    public bool IsFilesPanel => ActivePanel == DesignPanel.DiscFiles;

    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Rebuilds the canvas from the project; the panels follow through <see cref="Sync"/>.</summary>
    public void Refresh() => Canvas.Refresh();

    public void Reset()
    {
        Tone.CommitPendingEdits();
        Canvas.Reset();
        ActivePanel = DesignPanel.Layers;
        Status = null;
    }

    /// <summary>Commits any grouped edit (nudges, slider adjustments) before another command runs.</summary>
    public void CommitPendingEdits()
    {
        Canvas.CommitPendingEdits();
        Tone.CommitPendingEdits();
    }

    private void Sync()
    {
        Layers.Sync(Canvas.Layers, Canvas.SelectedLayerId);
        Properties.Sync(Canvas.SelectedLayerId);
        Properties.SetMissing(Canvas.SelectedLayer?.IsMissing == true);
        Tone.Sync();
        DiscFiles.Sync();
    }

    [RelayCommand]
    private void ShowPanel(DesignPanel panel) => ActivePanel = panel;

    [RelayCommand]
    private void Select(LayerListItem? item)
    {
        if (item is not null)
        {
            Canvas.SelectedLayerId = item.Id;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        CommitPendingEdits();
        IReadOnlyList<string> files;
        try
        {
            files = await _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
                _text["design.layers.importTitle"],
                AllowMultiple: true,
                SuggestedDirectory: Path.GetDirectoryName(_projects.ProjectFilePath),
                FileTypes: [ImageFiles]));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = _text["projectSettings.error.pickerUnavailable"];
            return;
        }

        if (files.Count > 0)
        {
            ImportFiles(files);
        }
    }

    /// <summary>Adds the files as layers above the current top layer, as one undoable step.</summary>
    public void ImportFiles(IReadOnlyList<string> files)
    {
        NewLayer[] layers = files
            .Select(file => new NewLayer(Path.GetFileNameWithoutExtension(file), Path.GetFullPath(file)))
            .ToArray();
        IReadOnlyList<string> added = [];
        if (_editor.Apply(HistoryLabels.AddLayers, workspace => DesignEdits.AddLayers(workspace, layers, out added)).Succeeded
            && added.Count > 0)
        {
            Canvas.Refresh();
            Canvas.SelectedLayerId = added[^1];
            Status = null;
        }
    }

    [RelayCommand]
    private void MoveUp(LayerListItem? item) => MoveBy(item, +1);

    [RelayCommand]
    private void MoveDown(LayerListItem? item) => MoveBy(item, -1);

    /// <summary>Moves a layer to a list position (top-first), used by drag and drop in the list.</summary>
    public void MoveToListIndex(string layerId, int listIndex)
    {
        CommitPendingEdits();
        int count = Layers.Items.Count;
        int documentIndex = count - 1 - Math.Clamp(listIndex, 0, count - 1);
        _editor.Apply(HistoryLabels.MoveLayer, workspace => DesignEdits.MoveLayer(workspace, layerId, documentIndex), [layerId]);
    }

    [RelayCommand]
    private void ToggleVisibility(LayerListItem? item)
    {
        if (item is null)
        {
            return;
        }

        CommitPendingEdits();
        bool visible = !item.Visible;
        _editor.Apply(HistoryLabels.LayerVisibility, workspace => DesignEdits.SetVisibility(workspace, item.Id, visible), [item.Id]);
    }

    [RelayCommand]
    private void Duplicate() => Canvas.DuplicateSelected();

    [RelayCommand]
    private void Remove() => Canvas.DeleteSelected();

    /// <summary>Points the selected layer at a new image file, as one undoable step.</summary>
    [RelayCommand]
    private async Task RelinkAsync()
    {
        CommitPendingEdits();
        string? layerId = Canvas.SelectedLayerId;
        if (layerId is null || _projects.Current is not { } workspace)
        {
            return;
        }

        IReadOnlyList<string> files;
        try
        {
            files = await _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
                _text["design.layers.relinkTitle"],
                SuggestedDirectory: Path.GetDirectoryName(_projects.ProjectFilePath),
                FileTypes: [ImageFiles]));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = _text["projectSettings.error.pickerUnavailable"];
            return;
        }

        if (files.FirstOrDefault() is not { } file)
        {
            return;
        }

        ProjectAssetReference? asset = _assets.Inspect(workspace, _projects.ProjectFilePath)
            .FirstOrDefault(reference => reference.Kind == ProjectAssetKind.LayerImage && reference.Key == layerId);
        if (asset is not null)
        {
            _editor.Apply(HistoryLabels.RelinkLayer, current => _assets.Relink(current, asset, file), [layerId]);
        }
    }

    private void MoveBy(LayerListItem? item, int towardTop)
    {
        if (item is null)
        {
            return;
        }

        CommitPendingEdits();
        Canvas.SelectedLayerId = item.Id;
        int target = item.DocumentIndex + towardTop;
        if (target < 0 || target >= Layers.Items.Count)
        {
            return;
        }

        _editor.Apply(HistoryLabels.MoveLayer, workspace => DesignEdits.MoveLayer(workspace, item.Id, target), [item.Id]);
    }
}
