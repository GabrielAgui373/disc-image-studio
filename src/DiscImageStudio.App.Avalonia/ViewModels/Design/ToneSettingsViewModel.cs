using System.Globalization;
using DiscImageStudio.App.Avalonia.Design;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Preview;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>
/// Tone controls (dither strength, levels, brightness, sRGB). A burst of changes from a slider or
/// the keyboard becomes one history step, committed after a short pause or before any other
/// command; switches and the reset commit at once. A before/after of the tone follows every
/// value at once, including slider positions not yet committed, on the latest composition.
/// </summary>
public sealed partial class ToneSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>Side of the before/after thumbnails, in pixels.</summary>
    public const int PreviewSize = 280;

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly ITextCatalog _text;
    private readonly Debouncer _commit;
    private readonly Debouncer _preview;
    private ProjectEditor.DragEdit? _adjustment;
    private bool _syncing;
    private ComposedDesign? _source;
    private long _previewGeneration;
    private bool _renderCanvas;

    public ToneSettingsViewModel(ProjectLifecycleService projects, ProjectEditor editor, ITextCatalog text)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _commit = new Debouncer(CommitDelay, CommitPendingEdits);
        _preview = new Debouncer(PreviewDelay, () => _ = RenderPreviewAsync());
        BeforeLabel = text["design.tone.before"];
        AfterLabel = text["design.tone.after"];
        Title = text["design.tone.title"];
        Description = text["design.tone.description"];
        DitherLabel = text["design.tone.dither"];
        BrightnessLabel = text["design.tone.brightness"];
        LevelsLabel = text["design.tone.levels"];
        TwoLevelsLabel = text["design.tone.twoLevels"];
        FourLevelsLabel = text["design.tone.fourLevels"];
        SrgbLabel = text["design.tone.srgb"];
        ResetLabel = text["design.tone.reset"];
        PreviewNote = text["design.tone.previewNote"];
    }

    public string Title { get; }

    public string Description { get; }

    public string DitherLabel { get; }

    public string BrightnessLabel { get; }

    public string LevelsLabel { get; }

    public string TwoLevelsLabel { get; }

    public string FourLevelsLabel { get; }

    public string SrgbLabel { get; }

    public string BeforeLabel { get; }

    public string AfterLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTonePreview))]
    public partial IImage? BeforeImage { get; private set; }

    [ObservableProperty]
    public partial IImage? AfterImage { get; private set; }

    public bool HasTonePreview => BeforeImage is not null;

    /// <summary>
    /// The whole composition through the tone, for the canvas's "view with tone" mode. Raised
    /// with <see langword="null"/> when there is no composition or the canvas stopped asking.
    /// </summary>
    public event Action<TonedCanvasImage?>? CanvasImageChanged;

    /// <summary>The latest composition (from the Design preview), or <see langword="null"/> when there is none.</summary>
    public void SetSource(ComposedDesign? source)
    {
        _source = source;
        if (source is null)
        {
            _preview.Cancel();
            ++_previewGeneration;
            BeforeImage = null;
            AfterImage = null;
            CanvasImageChanged?.Invoke(null);
            return;
        }

        _preview.Schedule();
    }

    /// <summary>Turns rendering of the full toned composition for the canvas on or off.</summary>
    public void SetCanvasRendering(bool enabled)
    {
        _renderCanvas = enabled;
        if (!enabled)
        {
            CanvasImageChanged?.Invoke(null);
        }
        else
        {
            SchedulePreview();
        }
    }

    public string ResetLabel { get; }

    public string PreviewNote { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DitherText))]
    public partial double DitherPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrightnessText))]
    public partial double Brightness { get; set; }

    [ObservableProperty]
    public partial bool IsFourLevels { get; set; }

    [ObservableProperty]
    public partial bool IsSrgbLinearized { get; set; }

    /// <summary>Four levels are a CD-only mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelsHint))]
    public partial bool CanUseFourLevels { get; private set; }

    public string LevelsHint => CanUseFourLevels ? _text["design.tone.levelsHintCd"] : _text["design.tone.levelsHintOther"];

    public string DitherText => string.Format(CultureInfo.CurrentUICulture, "{0:0}%", DitherPercent);

    public string BrightnessText => string.Format(CultureInfo.CurrentUICulture, "{0:+0;-0;0}", Brightness);

    public void Sync()
    {
        if (_adjustment is not null || _projects.Current is not { } workspace)
        {
            return;
        }

        ToneProfile tone = workspace.Tone.Profile;
        _syncing = true;
        try
        {
            CanUseFourLevels = workspace.Media is CdState;
            DitherPercent = Math.Round(tone.DitherStrength * 100);
            Brightness = Math.Round(tone.Brightness);
            IsFourLevels = tone.Levels == ToneProfile.FourLevels;
            IsSrgbLinearized = tone.SrgbLinearization;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnDitherPercentChanged(double value)
    {
        Adjust();
        SchedulePreview();
    }

    partial void OnBrightnessChanged(double value)
    {
        Adjust();
        SchedulePreview();
    }

    partial void OnIsFourLevelsChanged(bool value)
    {
        Switch();
        SchedulePreview();
    }

    partial void OnIsSrgbLinearizedChanged(bool value)
    {
        Switch();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        if (_source is not null)
        {
            _preview.Schedule();
        }
    }

    private async Task RenderPreviewAsync()
    {
        if (_source is not { } source || _projects.Current is not { } workspace)
        {
            return;
        }

        long generation = ++_previewGeneration;
        OpticalDiscFamily family = workspace.Media.Family;
        ToneProfile tone = Current();
        bool canvas = _renderCanvas;
        (IImage before, IImage after, IImage? full) = await Task.Run(() =>
        {
            BgraRaster raster = source.Image;
            (byte[] beforePixels, byte[] afterPixels) = TonePreviewRenderer.Render(raster, PreviewSize, family, tone);
            IImage? toned = null;
            if (canvas)
            {
                // The canvas zooms up to 12x, so it gets the composition's full resolution.
                byte[] fullPixels = TonePreviewRenderer.RenderToned(raster, raster.Width, family, tone);
                toned = CompositionBitmap.ToBitmap(new BgraRaster(raster.Width, raster.Width, fullPixels));
            }

            return ((IImage)CompositionBitmap.ToBitmap(new BgraRaster(PreviewSize, PreviewSize, beforePixels)),
                (IImage)CompositionBitmap.ToBitmap(new BgraRaster(PreviewSize, PreviewSize, afterPixels)),
                toned);
        });
        if (generation == _previewGeneration)
        {
            BeforeImage = before;
            AfterImage = after;
            if (full is not null && _renderCanvas)
            {
                CanvasImageChanged?.Invoke(new TonedCanvasImage(full, source.CanvasOuterRadiusMm, source.Design));
            }
        }
    }

    [RelayCommand]
    private void Reset()
    {
        CommitPendingEdits();
        _editor.Apply(HistoryLabels.Tone, workspace => DesignEdits.SetTone(workspace, ToneProfile.Photo));
        Sync();
    }

    /// <summary>Commits a pending slider adjustment; call before undo, save or other commands.</summary>
    public void CommitPendingEdits()
    {
        _commit.Cancel();
        ProjectEditor.DragEdit? adjustment = _adjustment;
        _adjustment = null;
        adjustment?.Commit();
    }

    private void Adjust()
    {
        if (_syncing)
        {
            return;
        }

        if (_adjustment is null)
        {
            if (!_editor.CanApply)
            {
                Sync();
                return;
            }

            _adjustment = _editor.BeginDrag(HistoryLabels.Tone);
        }

        ToneProfile tone = Current();
        _adjustment.Update(workspace => DesignEdits.SetTone(workspace, tone));
        _commit.Schedule();
    }

    private void Switch()
    {
        if (_syncing)
        {
            return;
        }

        CommitPendingEdits();
        ToneProfile tone = Current();
        if (!_editor.Apply(HistoryLabels.Tone, workspace => DesignEdits.SetTone(workspace, tone)).Succeeded)
        {
            Sync();
        }
    }

    private ToneProfile Current()
        => new(
            Math.Clamp(DitherPercent / 100, 0, 1),
            IsFourLevels && CanUseFourLevels ? ToneProfile.FourLevels : ToneProfile.TwoLevels,
            Math.Clamp(Brightness, ToneProfile.MinimumBrightness, ToneProfile.MaximumBrightness),
            IsSrgbLinearized);
}

/// <summary>The design composed through the tone, the canvas radius it covers and the design it shows.</summary>
public sealed record TonedCanvasImage(IImage Image, double CanvasOuterRadiusMm, DesignState Design);
