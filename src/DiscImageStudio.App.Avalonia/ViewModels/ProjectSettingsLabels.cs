using DiscImageStudio.App.Avalonia.Localization;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>Static, localized labels of the project settings dialog.</summary>
public sealed class ProjectSettingsLabels(ITextCatalog text)
{
    public string Title { get; } = text["projectSettings.title"];

    public string Description { get; } = text["projectSettings.description"];

    public string GeneralSection { get; } = text["projectSettings.section.general"];

    public string NameLabel { get; } = text["projectSettings.name.label"];

    public string FamilyLabel { get; } = text["projectSettings.family.label"];

    public string FamilyLocked { get; } = text["projectSettings.family.locked"];

    public string GeometrySection { get; } = text["projectSettings.section.geometry"];

    public string GeometryHint { get; } = text["projectSettings.geometry.hint"];

    public string CustomBadge { get; } = text["projectSettings.preset.customBadge"];

    public string NewPresetWatermark { get; } = text["projectSettings.preset.newName.watermark"];

    public string SavePreset { get; } = text["projectSettings.preset.save"];

    public string DeletePreset { get; } = text["projectSettings.preset.delete"];

    public string OutputSection { get; } = text["projectSettings.section.output"];

    public string VolumeLabel { get; } = text["projectSettings.volume.label"];

    public string OuterRing { get; } = text["projectSettings.outerRing.label"];

    public string OutputLabel { get; } = text["projectSettings.output.label"];

    public string DataLabel { get; } = text["projectSettings.data.label"];

    public string DataHint { get; } = text["projectSettings.data.hint"];

    public string Browse { get; } = text["projectSettings.browse"];

    public string AssetsSection { get; } = text["projectSettings.section.assets"];

    public string Relink { get; } = text["projectSettings.relink"];

    public string SearchFolder { get; } = text["projectSettings.search"];

    public string ApplyProposals { get; } = text["projectSettings.review.apply"];

    public string CancelReview { get; } = text["projectSettings.review.cancel"];

    public string Apply { get; } = text["projectSettings.apply"];

    public string Cancel { get; } = text["prompt.cancel"];
}
