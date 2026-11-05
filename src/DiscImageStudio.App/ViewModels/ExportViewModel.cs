using DiscImageStudio.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Core;
using DiscImageStudio.Generation;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>Export workspace: turns the design page's composite into the disc artifact file.
/// The disc family comes from the open project, so the CD and DVD parameter pages collapse
/// into this one; the generation geometry lives in the project settings. Streaming that same
/// material to a blank disc is the burn workspace's job. The tone parameters live with the
/// design page, next to the composite they affect; this workspace only passes the shared
/// values to the engine.</summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    public ExportViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !_shell.IsBusy);
        BrowseDataCommand = new RelayCommand(BrowseData, () => !_shell.IsBusy);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !_shell.IsBusy);
        _shell.BusyChanged += NotifyCommands;
        _shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ShellViewModel.IsProjectOpen)
                or nameof(ShellViewModel.CurrentProject))
            {
                OnPropertyChanged(nameof(IsCd));
                OnPropertyChanged(nameof(IsDvd));
                OnPropertyChanged(nameof(IsBluRay));
                OnPropertyChanged(nameof(FamilyLabel));
                OnPropertyChanged(nameof(GenerateButtonText));
                UpdateSourceSummary();
            }
        };
        _design.DesignEdited += UpdateSourceSummary;
        _design.CanvasChanged += UpdateSourceSummary;
        _state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DiscParametersState.CdDataDirectory))
            {
                OnPropertyChanged(nameof(GenerateButtonText));
            }
        };
    }

    public bool IsCd => _shell.CurrentProject?.IsCd ?? true;

    public bool IsDvd => _shell.CurrentProject?.IsDvd ?? false;

    public bool IsBluRay => _shell.CurrentProject?.IsBluRay ?? false;

    /// <summary>False while the design page has no layers; the source row then offers the
    /// jump to where the picture comes from instead of a dead end.</summary>
    public bool HasLayers => _design.Layers.Count > 0;

    /// <summary>Family chip in the page header.</summary>
    public string FamilyLabel => _shell.CurrentProject?.FamilyLabel ?? "CD";

    /// <summary>Primary action label: the artifact differs per family (WAV track vs ISO).</summary>
    public string GenerateButtonText => !IsCd || _state.HasCdFiles ? L.T("生成镜像") : L.T("生成音轨");

    /// <summary>The source card's jump into the project dialog, where the generation geometry
    /// is edited; every other workspace shows the same affordance for the same reason.</summary>
    public IAsyncRelayCommand OpenProjectSettingsCommand => _shell.EditProjectCommand;

    /// <summary>Workspace navigation for the page header's "下一步" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    public RelayCommand BrowseOutputCommand { get; }

    public RelayCommand BrowseDataCommand { get; }

    public IAsyncRelayCommand GenerateCommand { get; }

    [ObservableProperty]
    private string _sourceSummary = L.T("请先在设计页导入图片。");

    /// <summary>The action label and source row are the page's cached text; both are composed
    /// from the current state so they re-read cleanly.</summary>
    internal void Relocalize()
    {
        OnPropertyChanged(nameof(GenerateButtonText));
        UpdateSourceSummary();
    }

    private void NotifyCommands()
    {
        BrowseOutputCommand.NotifyCanExecuteChanged();
        BrowseDataCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        if (IsBluRay)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "bd-image.iso");
            if (iso is not null)
            {
                _state.BdOutputPath = iso;
            }
        }
        else if (IsDvd)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "dvd-image.iso");
            if (iso is not null)
            {
                _state.DvdOutputPath = iso;
            }
        }
        else if (_state.HasCdFiles)
        {
            string? iso = _shell.Dialogs?.PickSave(L.T("ISO 镜像|*.iso|所有文件|*.*"), ".iso", "cd-image.iso");
            if (iso is not null)
            {
                _state.CdOutputPath = iso;
            }
        }
        else
        {
            string? wav = _shell.Dialogs?.PickSave(L.T("WAV 音轨（推荐）|*.wav|RAW 音轨（旧格式）|*.raw|所有文件|*.*"), ".wav", "cd-track.wav");
            if (wav is not null)
            {
                _state.CdOutputPath = wav;
            }
        }
    }

    private void BrowseData()
    {
        if (IsCd)
        {
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入 CD 数据盘的文件夹"));
            if (picked is not null)
            {
                _state.CdDataDirectory = picked;
            }

            return;
        }

        if (IsBluRay)
        {
            string? picked = _shell.Dialogs?.PickFolder(L.T("选择要放入蓝光盘的文件夹"));
            if (picked is not null)
            {
                _state.BdDataDirectory = picked;
            }

            return;
        }

        string? dvdFolder = _shell.Dialogs?.PickFolder(L.T("选择要放入 DVD 的文件夹"));
        if (dvdFolder is not null)
        {
            _state.DvdDataDirectory = dvdFolder;
        }
    }

    private void UpdateSourceSummary()
    {
        if (!_shell.IsProjectOpen)
        {
            SourceSummary = L.T("请先打开或新建一个工程。");
            return;
        }

        int layerCount = _design.Layers.Count;
        SourceSummary = layerCount == 0
            ? L.T("请先在设计页导入图片。")
            : L.F($"设计页合成图源（{RingImageQuality.GenerationSize} px）");
        OnPropertyChanged(nameof(HasLayers));
    }

    private async Task GenerateAsync()
    {
        try
        {
            // The composite renders here at export quality even when the design page has not
            // been visited in this session, so a reopened project can generate immediately.
            string compositePath = await _design.EnsureExportCompositeAsync();
            if (IsBluRay)
            {
                await GenerateBluRayAsync(compositePath);
            }
            else if (IsDvd)
            {
                await GenerateDvdAsync(compositePath);
            }
            else
            {
                await GenerateCdAsync(compositePath);
            }
        }
        catch (Exception exception)
        {
            _shell.ShowValidationError(exception);
        }
    }

    private async Task GenerateCdAsync(string compositePath)
    {
        string output = RequirePath(_state.CdOutputPath,
            _state.HasCdFiles ? L.T("请选择 CD ISO 输出位置。") : L.T("请选择 CD 音轨输出位置。"));
        GenerateDiscRequest request = GenerationRequestFactory.CreateForProject(
            OpticalDiscFamily.CompactDisc,
            _state,
            compositePath,
            output);
        await _shell.RunDiscGenerationAsync(
            _state.HasCdFiles ? L.T("正在生成 CD 数据盘 ISO…") : L.T("正在生成 CD 音轨…"),
            request,
            output);
        // The generated track is the live preview page's read-back input.
        _state.CdTrackPath = output;
    }

    private async Task GenerateDvdAsync(string compositePath)
    {
        string output = RequirePath(_state.DvdOutputPath, L.T("请选择 DVD ISO 输出位置。"));
        GenerateDiscRequest request = GenerationRequestFactory.CreateForProject(
            OpticalDiscFamily.Dvd,
            _state,
            compositePath,
            output);
        await _shell.RunDiscGenerationAsync(
            L.T("正在生成 DVD ISO…"),
            request,
            output);
        // The generated ISO is the live preview page's read-back input.
        _state.DvdIsoPath = output;
    }

    private async Task GenerateBluRayAsync(string compositePath)
    {
        string output = RequirePath(_state.BdOutputPath, L.T("请选择蓝光 ISO 输出位置。"));
        GenerateDiscRequest request = GenerationRequestFactory.CreateForProject(
            OpticalDiscFamily.BluRay,
            _state,
            compositePath,
            output);
        await _shell.RunDiscGenerationAsync(
            L.T("正在生成蓝光 ISO…"),
            request,
            output);
        // The generated ISO and its sidecar are the live preview page's read-back input.
        _state.BdIsoPath = output;
    }

    private static string RequirePath(string raw, string message)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException(message) : value;
    }
}
