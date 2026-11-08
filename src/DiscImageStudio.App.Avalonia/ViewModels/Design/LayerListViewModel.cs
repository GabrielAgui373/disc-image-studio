using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscImageStudio.App.Avalonia.ViewModels.Design;

/// <summary>One row of the layer list. The list shows the top layer first.</summary>
public sealed partial class LayerListItem : ObservableObject
{
    public LayerListItem(string id) => Id = id;

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Visible { get; set; }

    [ObservableProperty]
    public partial bool IsMissing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial IImage? Thumbnail { get; set; }

    public bool HasThumbnail => Thumbnail is not null;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Bottom-first index in the project document.</summary>
    public int DocumentIndex { get; set; }
}

/// <summary>
/// Projection of the canvas layers for the list (top first). Rows are updated in place while the
/// order is unchanged, so a drag preview on the canvas never rebuilds the list under the pointer.
/// </summary>
public sealed class LayerListViewModel
{
    public ObservableCollection<LayerListItem> Items { get; } = [];

    public void Sync(IReadOnlyList<CanvasLayer> layers, string? selectedId)
    {
        // Canvas layers are bottom-first; the list is top-first.
        CanvasLayer[] topFirst = layers.Reverse().ToArray();
        bool sameOrder = topFirst.Length == Items.Count
                         && topFirst.Select(layer => layer.Id).SequenceEqual(Items.Select(item => item.Id));
        if (!sameOrder)
        {
            Items.Clear();
            foreach (CanvasLayer layer in topFirst)
            {
                Items.Add(new LayerListItem(layer.Id));
            }
        }

        for (int index = 0; index < topFirst.Length; index++)
        {
            CanvasLayer layer = topFirst[index];
            LayerListItem item = Items[index];
            item.Name = layer.Name;
            item.Visible = layer.Visible;
            item.IsMissing = layer.IsMissing;
            item.Thumbnail = layer.Image;
            item.IsSelected = layer.Id == selectedId;
            item.DocumentIndex = topFirst.Length - 1 - index;
        }
    }
}
