using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.App.Avalonia.Platform;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Application.Settings;
using DiscImageStudio.Core;

namespace DiscImageStudio.App.Avalonia.ViewModels;

/// <summary>
/// One recent-project card. It is shown immediately from the history entry and updated when
/// the background probe reports the file's state and thumbnail.
/// </summary>
public sealed partial class RecentProjectItem : ObservableObject
{
    public RecentProjectItem(RecentProjectEntry entry, string openedLabel)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        OpenedLabel = openedLabel;
        Name = entry.Name;
        Family = RecentProjectsService.ToFamily(entry.DiscFamily);
    }

    public RecentProjectEntry Entry { get; }

    public string ProjectPath => Entry.ProjectPath;

    public string OpenedLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FamilyVariant), nameof(FamilyBadge))]
    public partial OpticalDiscFamily Family { get; private set; }

    public string FamilyVariant => RecentProjectsService.ToFamilyToken(Family);

    public string FamilyBadge => Family switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    [ObservableProperty]
    public partial string Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(HasIssue))]
    public partial bool IsProbing { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(HasIssue))]
    public partial RecentProjectStatus Status { get; private set; }

    [ObservableProperty]
    public partial string StatusLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial IImage? Thumbnail { get; private set; }

    public bool HasThumbnail => Thumbnail is not null;

    public bool IsAvailable => !IsProbing && Status == RecentProjectStatus.Available;

    public bool HasIssue => !IsProbing && Status != RecentProjectStatus.Available;

    internal void Apply(RecentProjectProbe probe, IImage? thumbnail, string statusLabel)
    {
        Name = probe.Name;
        Family = probe.Family;
        Status = probe.Status;
        StatusLabel = statusLabel;
        Thumbnail = thumbnail;
        IsProbing = false;
    }
}

/// <summary>
/// Home's recent-project list. Loading and probing run off the UI thread; a newer refresh
/// supersedes older probes so a slow file system cannot write stale cards.
/// </summary>
public sealed partial class RecentProjectsViewModel : ObservableObject
{
    public const int ThumbnailDecodeWidth = 176;

    private readonly RecentProjectsService _service;
    private readonly IThumbnailDecoder _decoder;
    private readonly ITextCatalog _text;
    private readonly Func<string, Task<bool>> _openProject;
    private int _generation;

    public RecentProjectsViewModel(
        RecentProjectsService service,
        IThumbnailDecoder decoder,
        ITextCatalog text,
        Func<string, Task<bool>> openProject)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _openProject = openProject ?? throw new ArgumentNullException(nameof(openProject));
        RemoveLabel = text["recent.action.remove"];
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public ObservableCollection<RecentProjectItem> Items { get; } = [];

    public string RemoveLabel { get; }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public async Task RefreshAsync()
    {
        int generation = ++_generation;
        IsLoading = Items.Count == 0;
        IReadOnlyList<RecentProjectEntry> entries;
        try
        {
            entries = await Task.Run(_service.Load);
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }

        if (generation != _generation)
        {
            return;
        }

        Items.Clear();
        foreach (RecentProjectEntry entry in entries)
        {
            Items.Add(new RecentProjectItem(entry, FormatOpened(entry.LastOpenedUtc)));
        }

        await Task.WhenAll(Items.ToArray().Select(item => ProbeAsync(item, generation)));
    }

    [RelayCommand]
    private async Task OpenAsync(RecentProjectItem? item)
    {
        if (item is null || item.IsProbing)
        {
            return;
        }

        Status = null;
        if (item.Status == RecentProjectStatus.Missing)
        {
            Status = Format("recent.error.missing", item.Name);
            return;
        }

        if (!await _openProject(item.ProjectPath))
        {
            // The open failed for a reason the probe may not have seen (for example a file
            // replaced since startup); probe again so the card shows its current state.
            await ProbeAsync(item, _generation);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(RecentProjectItem? item)
    {
        if (item is null)
        {
            return;
        }

        await Task.Run(() => _service.Remove(item.ProjectPath));
        Items.Remove(item);
        Status = Format("recent.removed", item.Name);
    }

    private async Task ProbeAsync(RecentProjectItem item, int generation)
    {
        RecentProjectProbe probe;
        IImage? thumbnail;
        try
        {
            (probe, thumbnail) = await Task.Run(() =>
            {
                RecentProjectProbe result = _service.Probe(item.Entry);
                return (result, result.ThumbnailPng is { } png ? _decoder.Decode(png, ThumbnailDecodeWidth) : null);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            probe = new RecentProjectProbe(item.Entry, RecentProjectStatus.Invalid, item.Name, item.Family, null);
            thumbnail = null;
        }

        if (generation == _generation)
        {
            item.Apply(probe, thumbnail, StatusText(probe.Status));
        }
    }

    private string StatusText(RecentProjectStatus status)
        => status switch
        {
            RecentProjectStatus.Missing => _text["recent.status.missing"],
            RecentProjectStatus.AccessDenied => _text["recent.status.accessDenied"],
            RecentProjectStatus.UnsupportedSchema => _text["recent.status.unsupportedSchema"],
            RecentProjectStatus.Invalid => _text["recent.status.invalid"],
            _ => string.Empty,
        };

    private string FormatOpened(DateTimeOffset openedUtc)
        => string.Format(
            CultureInfo.CurrentUICulture,
            _text["recent.opened"],
            openedUtc.ToLocalTime().ToString(_text["recent.dateFormat"], CultureInfo.CurrentUICulture));

    private string Format(string key, string argument)
        => string.Format(CultureInfo.CurrentUICulture, _text[key], argument);
}
