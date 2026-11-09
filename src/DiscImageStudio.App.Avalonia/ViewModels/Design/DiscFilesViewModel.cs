using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>
/// The folder whose files are written to the disc's file system (ISO 9660/Joliet or UDF). It is
/// deliberately presented apart from the image layers: these files are stored as data, never
/// drawn. Choosing or clearing the folder is one history step; the summary is computed off the UI
/// thread with a bounded scan.
/// </summary>
public sealed partial class DiscFilesViewModel : ObservableObject
{
    private const int MaximumScannedEntries = 100_000;

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly IFilePickerService _filePicker;
    private readonly IExternalLauncher _launcher;
    private readonly ITextCatalog _text;
    private string? _summarizedFolder;
    private int _generation;

    public DiscFilesViewModel(
        ProjectLifecycleService projects,
        ProjectEditor editor,
        IFilePickerService filePicker,
        IExternalLauncher launcher,
        ITextCatalog text)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        Title = text["design.files.title"];
        Description = text["design.files.description"];
        EmptyText = text["design.files.empty"];
        ChooseLabel = text["design.files.choose"];
        ClearLabel = text["design.files.clear"];
        OpenLabel = text["design.files.open"];
        DistinctionNote = text["design.files.note"];
    }

    public string Title { get; }

    public string Description { get; }

    public string EmptyText { get; }

    public string ChooseLabel { get; }

    public string ClearLabel { get; }

    public string OpenLabel { get; }

    public string DistinctionNote { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(CanOpen))]
    public partial string FolderPath { get; private set; } = string.Empty;

    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    public partial bool IsMissing { get; private set; }

    public bool CanOpen => HasFolder && !IsMissing;

    public void Sync()
    {
        ProjectWorkspace? workspace = _projects.Current;
        string stored = workspace?.Delivery.DataDirectory ?? string.Empty;
        string resolved = string.IsNullOrWhiteSpace(stored)
            ? string.Empty
            : ProjectPaths.ResolveAsset(_projects.ProjectFilePath, stored) ?? stored;
        FolderPath = resolved;
        if (resolved == _summarizedFolder)
        {
            return;
        }

        _summarizedFolder = resolved;
        _ = SummarizeAsync(resolved);
    }

    [RelayCommand]
    private async Task ChooseAsync()
    {
        string? folder;
        try
        {
            folder = await _filePicker.OpenFolderAsync(new FolderPickerRequest(
                _text["design.files.pickerTitle"],
                HasFolder ? FolderPath : Path.GetDirectoryName(_projects.ProjectFilePath)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Summary = _text["projectSettings.error.pickerUnavailable"];
            return;
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            _editor.Apply(HistoryLabels.DataFolder, workspace => DesignEdits.SetDataDirectory(workspace, folder));
        }
    }

    [RelayCommand]
    private void Clear()
        => _editor.Apply(HistoryLabels.DataFolder, workspace => DesignEdits.SetDataDirectory(workspace, null));

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (HasFolder && !IsMissing)
        {
            await _launcher.OpenPathAsync(FolderPath);
        }
    }

    private async Task SummarizeAsync(string folder)
    {
        int generation = ++_generation;
        if (string.IsNullOrWhiteSpace(folder))
        {
            IsMissing = false;
            Summary = EmptyText;
            return;
        }

        Summary = _text["design.files.counting"];
        (bool exists, long files, long bytes, bool truncated) = await Task.Run(() => Scan(folder));
        if (generation != _generation)
        {
            return;
        }

        IsMissing = !exists;
        Summary = !exists
            ? _text["design.files.missing"]
            : string.Format(
                CultureInfo.CurrentUICulture,
                _text[truncated ? "design.files.summaryPartial" : "design.files.summary"],
                files,
                FormatBytes(bytes));
    }

    private static (bool Exists, long Files, long Bytes, bool Truncated) Scan(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return (false, 0, 0, false);
        }

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long files = 0;
        long bytes = 0;
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if (++files > MaximumScannedEntries)
            {
                return (true, files - 1, bytes, true);
            }

            bytes += file.Length;
        }

        return (true, files, bytes, false);
    }

    private static string FormatBytes(long bytes) => ByteSize.Format(bytes);
}
