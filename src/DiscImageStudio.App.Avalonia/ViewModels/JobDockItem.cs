using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.App.Avalonia.ViewModels;

public enum JobPresentationState
{
    Running,
    Succeeded,
    Canceled,
    Failed,
}

public partial class JobDockItem(Guid id, string name) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial JobPresentationState State { get; set; } = JobPresentationState.Running;

    [ObservableProperty]
    public partial string Detail { get; set; } = "Starting…";

    [ObservableProperty]
    public partial double? Progress { get; set; }

    public bool IsRunning => State == JobPresentationState.Running;

    public bool HasProgress => Progress is not null;

    partial void OnStateChanged(JobPresentationState value)
        => OnPropertyChanged(nameof(IsRunning));

    partial void OnProgressChanged(double? value)
        => OnPropertyChanged(nameof(HasProgress));
}
