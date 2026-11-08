using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Ports;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Results;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

public sealed class ProjectAssetItem
{
    public ProjectAssetItem(ProjectAssetReference reference, string label, string kindLabel, string statusLabel)
    {
        Reference = reference;
        Label = label;
        KindLabel = kindLabel;
        StatusLabel = statusLabel;
    }

    public ProjectAssetReference Reference { get; }

    public string Label { get; }

    public string KindLabel { get; }

    public string StatusLabel { get; }

    public string Path => Reference.ResolvedPath;

    public bool IsMissing => !Reference.Exists;

    /// <summary>Layer rows show their kind; a data folder or calibration row is already named by it.</summary>
    public bool ShowKindLabel => !string.Equals(Label, KindLabel, StringComparison.Ordinal);
}

public sealed partial class RelinkProposalItem : ObservableObject
{
    public RelinkProposalItem(RelinkProposal proposal, string assetLabel, string matchLabel)
    {
        Proposal = proposal;
        AssetLabel = assetLabel;
        MatchLabel = matchLabel;
        // Only a unique exact-name match is pre-selected; a case-only match may be another file.
        IsAccepted = proposal.Match == RelinkMatch.Exact;
    }

    public RelinkProposal Proposal { get; }

    public string AssetLabel { get; }

    public string MatchLabel { get; }

    public string CandidatePath => Proposal.CandidatePath ?? string.Empty;

    public bool CanAccept => Proposal.CandidatePath is not null;

    public bool NeedsAttention => Proposal.Match != RelinkMatch.Exact;

    [ObservableProperty]
    public partial bool IsAccepted { get; set; }
}

/// <summary>
/// Project settings, user geometry presets and missing-asset recovery. All edits, including
/// relinks, change a draft copy of the workspace; nothing reaches the open project until Apply,
/// and Cancel discards the draft. User files are only ever referenced, never copied or moved.
/// </summary>
public sealed partial class ProjectSettingsViewModel : ObservableObject
{
    private const string CurrentGeometryId = "current-geometry";

    private static readonly FilePickerType ImageFileType = new(
        "Images",
        ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp"],
        ["image/*"]);

    private static readonly FilePickerType CalibrationFileType = new("Calibration session", ["*.json", "*"]);

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor? _editor;
    private readonly ProjectAssetService _assets;
    private readonly UserPresetCatalog _presets;
    private readonly IFilePickerService _filePicker;
    private readonly ITextCatalog _text;
    private readonly Func<OpticalDiscFamily, IReadOnlyList<NewProjectPresetOption>> _standardPresets;
    private ProjectWorkspace? _original;
    private ProjectWorkspace? _draft;
    private string? _projectFilePath;
    private bool _syncingDraft;

    public ProjectSettingsViewModel(
        ProjectLifecycleService projects,
        ProjectAssetService assets,
        UserPresetCatalog presets,
        IFilePickerService filePicker,
        ITextCatalog text,
        Func<OpticalDiscFamily, IReadOnlyList<NewProjectPresetOption>> standardPresets,
        ProjectEditor? editor = null)
    {
        _editor = editor;
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
        _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _standardPresets = standardPresets ?? throw new ArgumentNullException(nameof(standardPresets));
        Labels = new ProjectSettingsLabels(text);
        Assets.CollectionChanged += (_, _) => NotifyAssetSummary();
    }

    public ProjectSettingsLabels Labels { get; }

    public event EventHandler? UserPresetsChanged;

    public ObservableCollection<NewProjectPresetOption> Presets { get; } = [];

    public ObservableCollection<ProjectAssetItem> Assets { get; } = [];

    public ObservableCollection<RelinkProposalItem> Proposals { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FamilyTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FamilyVariant { get; private set; } = "cd";

    [ObservableProperty]
    public partial string VolumeLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FilesAtOuterRing { get; set; }

    [ObservableProperty]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DataDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedPreset), nameof(GeometrySummary))]
    public partial NewProjectPresetOption? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial string NewPresetName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool IsReviewingProposals { get; private set; }

    [ObservableProperty]
    public partial string ReviewSummary { get; private set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool CanDeleteSelectedPreset => SelectedPreset?.IsUserPreset == true;

    public bool HasAssets => Assets.Count > 0;

    public int MissingAssetCount => Assets.Count(asset => asset.IsMissing);

    public bool HasMissingAssets => MissingAssetCount > 0;

    public string AssetSummary => !HasAssets
        ? _text["projectSettings.assets.none"]
        : HasMissingAssets
            ? Format("projectSettings.assets.missingSummary", MissingAssetCount)
            : _text["projectSettings.assets.allFound"];

    public string GeometrySummary
    {
        get
        {
            MediaState? media = SelectedPreset?.Media ?? _draft?.Media;
            return media is null
                ? string.Empty
                : string.Format(
                    CultureInfo.CurrentUICulture,
                    _text["projectSettings.geometry.summary"],
                    media.InnerRadiusMm,
                    media.OuterRadiusMm,
                    media.Sectors);
        }
    }

    /// <summary>Opens the dialog on a draft of the current project.</summary>
    public async Task OpenAsync()
    {
        ProjectWorkspace? current = _projects.Current;
        if (current is null)
        {
            return;
        }

        _original = current.DeepCopy();
        _draft = current.DeepCopy();
        _projectFilePath = _projects.ProjectFilePath;
        _syncingDraft = true;
        try
        {
            Name = current.Identity.Name;
            VolumeLabel = current.Delivery.VolumeLabel;
            FilesAtOuterRing = current.Delivery.FilesAtOuterRing;
            OutputPath = current.Delivery.OutputPath;
            DataDirectory = current.Delivery.DataDirectory;
        }
        finally
        {
            _syncingDraft = false;
        }

        FamilyVariant = RecentProjectsService.ToFamilyToken(current.Media.Family);
        FamilyTitle = _text[$"projectSettings.family.{FamilyVariant}"];
        NewPresetName = string.Empty;
        Error = null;
        Status = null;
        CancelReview();
        ReloadPresets(selectId: CurrentGeometryId);
        IsOpen = true;
        await RefreshAssetsAsync();
    }

    /// <summary>
    /// Searches <paramref name="folder"/> for the missing assets and shows the proposals for
    /// review. This is the step after the folder picker; it never changes the draft by itself.
    /// </summary>
    public async Task ReviewFolderAsync(string folder)
    {
        ProjectAssetReference[] missing = Assets.Where(asset => asset.IsMissing).Select(asset => asset.Reference).ToArray();
        if (missing.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            RelinkSearchResult result = await Task.Run(() => _assets.FindInFolder(missing, folder));
            Proposals.Clear();
            foreach (RelinkProposal proposal in result.Proposals)
            {
                Proposals.Add(new RelinkProposalItem(proposal, AssetLabel(proposal.Asset), MatchLabel(proposal)));
            }

            int found = result.Proposals.Count(proposal => proposal.CandidatePath is not null);
            ReviewSummary = Format("projectSettings.review.summary", found, result.Proposals.Count)
                + (result.ReachedScanLimit ? " " + _text["projectSettings.review.scanLimit"] : string.Empty);
            IsReviewingProposals = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = _text["projectSettings.error.searchFailed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnDataDirectoryChanged(string value)
    {
        if (_syncingDraft || _draft is null)
        {
            return;
        }

        // The data folder is also an asset: keep the draft in step so its status is current.
        _draft = _draft with
        {
            Delivery = new DeliveryState(
                _draft.Delivery.OutputPath,
                value.Trim(),
                _draft.Delivery.VolumeLabel,
                _draft.Delivery.FilesAtOuterRing),
        };
        _ = RefreshAssetsAsync();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsOpen = false;
        CancelReview();
        _original = null;
        _draft = null;
    }

    [RelayCommand]
    private void Apply()
    {
        if (_draft is null || _original is null)
        {
            return;
        }

        Error = null;
        string name = Name.Trim();
        if (name.Length == 0)
        {
            Error = _text["projectSettings.error.nameRequired"];
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            Error = _text["projectSettings.error.outputRequired"];
            return;
        }

        ProjectWorkspace updated;
        try
        {
            MediaState media = SelectedPreset is { Id: not CurrentGeometryId } preset
                ? UserPresetCatalog.ApplyGeometry(_draft.Media, preset.Media)
                : _draft.Media;
            updated = _draft with
            {
                Identity = new ProjectIdentity(
                    name,
                    _draft.Identity.CreatedUtc,
                    _draft.Identity.ModifiedUtc,
                    _draft.Identity.CalibrationSessionPath,
                    _draft.Identity.ThumbnailPngBase64),
                Delivery = new DeliveryState(OutputPath.Trim(), DataDirectory.Trim(), VolumeLabel.Trim(), FilesAtOuterRing),
                Media = media,
            };
        }
        catch (ArgumentException)
        {
            Error = _text["projectSettings.error.invalid"];
            return;
        }

        if (!HasChanges(_original, updated))
        {
            Cancel();
            return;
        }

        // Through the editor the whole dialog is one undoable step ("Undo Project settings").
        ApplicationError? failure = _editor is not null
            ? _editor.Apply(HistoryLabels.ProjectSettings, _ => updated).Error
            : _projects.ReplaceWorkspace(updated, "project-settings", "project-settings-dialog").Error;
        if (failure is not null)
        {
            Error = failure.Code == ApplicationErrorCode.NoProjectOpen
                ? _text["projectSettings.error.noProject"]
                : _text["projectSettings.error.invalid"];
            return;
        }

        Cancel();
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await PickAsync(() => _filePicker.SaveFileAsync(new SaveFilePickerRequest(
            _text["projectSettings.output.pickerTitle"],
            SuggestedFileName: System.IO.Path.GetFileName(OutputPath),
            SuggestedDirectory: DirectoryOf(OutputPath))));
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseDataDirectoryAsync()
    {
        string? path = await PickAsync(() => _filePicker.OpenFolderAsync(new FolderPickerRequest(
            _text["projectSettings.data.pickerTitle"],
            DirectoryOf(_projectFilePath))));
        if (!string.IsNullOrWhiteSpace(path))
        {
            DataDirectory = path;
        }
    }

    [RelayCommand]
    private async Task RelinkAsync(ProjectAssetItem? item)
    {
        if (item is null || _draft is null)
        {
            return;
        }

        ProjectAssetReference asset = item.Reference;
        string? suggested = DirectoryOf(_projectFilePath);
        string? path = asset.IsDirectory
            ? await PickAsync(() => _filePicker.OpenFolderAsync(new FolderPickerRequest(
                Format("projectSettings.relink.pickerTitle", item.Label),
                suggested)))
            : (await PickManyAsync(() => _filePicker.OpenFilesAsync(new OpenFilePickerRequest(
                Format("projectSettings.relink.pickerTitle", item.Label),
                SuggestedDirectory: suggested,
                FileTypes: asset.Kind == ProjectAssetKind.LayerImage ? [ImageFileType] : [CalibrationFileType]))))
              .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (TryRelink(asset, path))
        {
            Status = Format("projectSettings.relink.done", item.Label);
            await RefreshAssetsAsync();
        }
        else
        {
            Error = _text["projectSettings.error.relinkKind"];
        }
    }

    [RelayCommand]
    private async Task SearchFolderAsync()
    {
        string? folder = await PickAsync(() => _filePicker.OpenFolderAsync(new FolderPickerRequest(
            _text["projectSettings.review.pickerTitle"],
            DirectoryOf(_projectFilePath))));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await ReviewFolderAsync(folder);
        }
    }

    [RelayCommand]
    private async Task ApplyProposalsAsync()
    {
        int applied = 0;
        int failed = 0;
        foreach (RelinkProposalItem proposal in Proposals.Where(item => item.IsAccepted && item.CanAccept))
        {
            if (TryRelink(proposal.Proposal.Asset, proposal.CandidatePath))
            {
                applied++;
            }
            else
            {
                failed++;
            }
        }

        CancelReview();
        Status = Format("projectSettings.review.applied", applied)
                 + (failed > 0 ? " " + Format("projectSettings.review.failed", failed) : string.Empty);
        await RefreshAssetsAsync();
    }

    [RelayCommand]
    private void CancelReview()
    {
        IsReviewingProposals = false;
        Proposals.Clear();
        ReviewSummary = string.Empty;
    }

    [RelayCommand]
    private void SavePreset()
    {
        if (_draft is null)
        {
            return;
        }

        Error = null;
        MediaState media = SelectedPreset is { Id: not CurrentGeometryId } preset
            ? UserPresetCatalog.ApplyGeometry(_draft.Media, preset.Media)
            : _draft.Media;
        ApplicationResult<ProjectPresetDefinition> saved = _presets.SaveUserPreset(
            NewPresetName,
            Format("projectSettings.preset.savedFrom", Name.Trim()),
            media);
        if (!saved.Succeeded)
        {
            Error = saved.Error?.Code switch
            {
                ApplicationErrorCode.InvalidRequest => _text["projectSettings.error.presetName"],
                ApplicationErrorCode.PresetReadFailed => _text["projectSettings.error.presetFileUnreadable"],
                _ => _text["projectSettings.error.presetWrite"],
            };
            return;
        }

        NewPresetName = string.Empty;
        Status = Format("projectSettings.preset.saved", saved.Value.DisplayName);
        ReloadPresets(selectId: saved.Value.Id);
        UserPresetsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsUserPreset: true } preset)
        {
            return;
        }

        ApplicationResult deleted = _presets.DeleteUserPreset(preset.Id);
        if (!deleted.Succeeded)
        {
            Error = deleted.Error?.Code == ApplicationErrorCode.PresetReadFailed
                ? _text["projectSettings.error.presetFileUnreadable"]
                : _text["projectSettings.error.presetWrite"];
            return;
        }

        Status = Format("projectSettings.preset.deleted", preset.Title);
        ReloadPresets(selectId: CurrentGeometryId);
        UserPresetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryRelink(ProjectAssetReference asset, string path)
    {
        if (_draft is null)
        {
            return false;
        }

        try
        {
            _draft = _assets.Relink(_draft, asset, path);
            if (asset.Kind == ProjectAssetKind.DataDirectory)
            {
                _syncingDraft = true;
                DataDirectory = _draft.Delivery.DataDirectory;
                _syncingDraft = false;
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task RefreshAssetsAsync()
    {
        ProjectWorkspace? draft = _draft;
        if (draft is null)
        {
            return;
        }

        string? projectPath = _projectFilePath;
        IReadOnlyList<ProjectAssetReference> references = await Task.Run(() => _assets.Inspect(draft, projectPath));
        if (!ReferenceEquals(draft, _draft))
        {
            return;
        }

        Assets.Clear();
        foreach (ProjectAssetReference reference in references.OrderByDescending(reference => !reference.Exists))
        {
            Assets.Add(new ProjectAssetItem(
                reference,
                AssetLabel(reference),
                _text[$"projectSettings.asset.kind.{reference.Kind}"],
                reference.Exists ? _text["projectSettings.asset.found"] : _text["projectSettings.asset.missing"]));
        }
    }

    private void ReloadPresets(string? selectId)
    {
        OpticalDiscFamily family = _draft?.Media.Family ?? OpticalDiscFamily.CompactDisc;
        Presets.Clear();
        if (_draft is not null)
        {
            Presets.Add(new NewProjectPresetOption(
                CurrentGeometryId,
                family,
                _text["projectSettings.preset.current"],
                _text["projectSettings.preset.currentDescription"],
                _draft.Media));
        }

        foreach (NewProjectPresetOption preset in _standardPresets(family))
        {
            Presets.Add(preset);
        }

        foreach (ProjectPresetDefinition preset in _presets.LoadUserPresets().Where(preset => preset.Family == family))
        {
            Presets.Add(NewProjectPresetOption.FromUserPreset(preset));
        }

        SelectedPreset = Presets.FirstOrDefault(preset => preset.Id == selectId) ?? Presets.FirstOrDefault();
    }

    private void NotifyAssetSummary()
    {
        OnPropertyChanged(nameof(HasAssets));
        OnPropertyChanged(nameof(MissingAssetCount));
        OnPropertyChanged(nameof(HasMissingAssets));
        OnPropertyChanged(nameof(AssetSummary));
    }

    private string AssetLabel(ProjectAssetReference reference)
        => reference.Kind == ProjectAssetKind.LayerImage
            ? reference.DisplayName
            : _text[$"projectSettings.asset.kind.{reference.Kind}"];

    private string MatchLabel(RelinkProposal proposal)
        => proposal.Match switch
        {
            RelinkMatch.Exact => _text["projectSettings.review.exact"],
            RelinkMatch.CaseDiffers => _text["projectSettings.review.caseDiffers"],
            RelinkMatch.Ambiguous => Format("projectSettings.review.ambiguous", proposal.CandidateCount),
            _ => _text["projectSettings.review.notFound"],
        };

    private async Task<string?> PickAsync(Func<Task<string?>> pick)
    {
        try
        {
            return await pick();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = _text["projectSettings.error.pickerUnavailable"];
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> PickManyAsync(Func<Task<IReadOnlyList<string>>> pick)
    {
        try
        {
            return await pick();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = _text["projectSettings.error.pickerUnavailable"];
            return [];
        }
    }

    /// <summary>Compares persisted content only, so reopening and applying unchanged does not mark the project dirty.</summary>
    private static bool HasChanges(ProjectWorkspace original, ProjectWorkspace updated)
        => !string.Equals(
            JsonSerializer.Serialize(ProjectDocumentMapper.ToDocument(original)),
            JsonSerializer.Serialize(ProjectDocumentMapper.ToDocument(updated)),
            StringComparison.Ordinal);

    private static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string Format(string key, params object[] arguments)
        => string.Format(CultureInfo.CurrentUICulture, _text[key], arguments);
}
