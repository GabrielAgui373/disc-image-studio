using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// Presentation-only choice shown on Home before the project creation workflow is opened.
/// The selected option is intentionally not persisted or used to create a project here.
/// </summary>
public sealed partial class HomeTemplateOption : ObservableObject
{
    public HomeTemplateOption(
        string id,
        string title,
        string description,
        string capacity,
        string badge,
        string selectedLabel,
        OpticalDiscFamily family)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("A template id is required.", nameof(id))
            : id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("A template title is required.", nameof(title))
            : title;
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("A template description is required.", nameof(description))
            : description;
        Capacity = string.IsNullOrWhiteSpace(capacity)
            ? throw new ArgumentException("A template capacity is required.", nameof(capacity))
            : capacity;
        Badge = string.IsNullOrWhiteSpace(badge)
            ? throw new ArgumentException("A template badge is required.", nameof(badge))
            : badge;
        SelectedLabel = string.IsNullOrWhiteSpace(selectedLabel)
            ? throw new ArgumentException("A selected label is required.", nameof(selectedLabel))
            : selectedLabel;
        Family = family;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public string Capacity { get; }

    public string Badge { get; }

    public string SelectedLabel { get; }

    public OpticalDiscFamily Family { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
