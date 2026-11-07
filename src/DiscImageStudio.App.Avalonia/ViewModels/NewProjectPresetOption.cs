using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// A localized presentation of a portable catalog preset. The media state itself remains a
/// validated application type and is handed to the lifecycle service only after confirmation.
/// </summary>
public sealed record NewProjectPresetOption(
    string Id,
    OpticalDiscFamily Family,
    string Title,
    string Description,
    MediaState Media);
