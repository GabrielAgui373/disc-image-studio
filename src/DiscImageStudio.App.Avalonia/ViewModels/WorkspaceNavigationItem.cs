using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Application.Navigation;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// Presentation projection of a workspace route. The route rules themselves remain
/// in the application layer's <see cref="NavigationState"/>.
/// </summary>
public partial class WorkspaceNavigationItem : ObservableObject
{
    public WorkspaceNavigationItem(
        WorkspacePage page,
        string label,
        string shortcut,
        ICommand navigateCommand)
    {
        Page = page;
        Label = label;
        Shortcut = shortcut;
        NavigateCommand = navigateCommand;
    }

    public WorkspacePage Page { get; }

    public string Label { get; }

    public string Shortcut { get; }

    public ICommand NavigateCommand { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    public string ToolTip => IsAvailable
        ? $"{Label} ({Shortcut})"
        : $"{Label} requires an open project ({Shortcut})";

    partial void OnIsAvailableChanged(bool value)
        => OnPropertyChanged(nameof(ToolTip));
}
