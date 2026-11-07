using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// A localized presentation of a portable catalog preset. The media state itself remains a
/// validated application type and is handed to the lifecycle service only after confirmation.
/// User presets keep the name and description the user wrote.
/// </summary>
public sealed record NewProjectPresetOption(
    string Id,
    OpticalDiscFamily Family,
    string Title,
    string Description,
    MediaState Media,
    bool IsUserPreset = false)
{
    public static NewProjectPresetOption FromUserPreset(ProjectPresetDefinition preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new NewProjectPresetOption(
            preset.Id,
            preset.Family,
            preset.DisplayName,
            preset.Description,
            preset.Media,
            IsUserPreset: true);
    }
}
