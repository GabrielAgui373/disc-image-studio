using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Editing;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>
/// Tone controls (dither strength, levels, brightness, sRGB). A burst of changes from a slider or
/// the keyboard becomes one history step, committed after a short pause or before any other
/// command; switches and the reset commit at once.
/// </summary>
public sealed partial class ToneSettingsViewModel : ObservableObject
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(600);

    private readonly ProjectLifecycleService _projects;
    private readonly ProjectEditor _editor;
    private readonly ITextCatalog _text;
    private readonly DispatcherTimer _commitTimer;
    private ProjectEditor.DragEdit? _adjustment;
    private bool _syncing;

    public ToneSettingsViewModel(ProjectLifecycleService projects, ProjectEditor editor, ITextCatalog text)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _commitTimer = new DispatcherTimer { Interval = CommitDelay };
        _commitTimer.Tick += (_, _) => CommitPendingEdits();
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

    partial void OnDitherPercentChanged(double value) => Adjust();

    partial void OnBrightnessChanged(double value) => Adjust();

    partial void OnIsFourLevelsChanged(bool value) => Switch();

    partial void OnIsSrgbLinearizedChanged(bool value) => Switch();

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
        _commitTimer.Stop();
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
        _commitTimer.Stop();
        _commitTimer.Start();
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
