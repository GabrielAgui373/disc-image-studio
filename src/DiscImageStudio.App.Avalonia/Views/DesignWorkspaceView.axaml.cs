using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DiscImageStudio.App.Avalonia.ViewModels.Design;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>
/// Layout and gestures of the Design page that are pure view concerns: switching between two
/// panes and a stacked layout, committing the layer name like a field, and reordering layers by
/// dragging a row's handle. All project changes go through the view model.
/// </summary>
public partial class DesignWorkspaceView : UserControl
{
    // Below this content width the canvas would get narrower than ~500 DIP next to the panel.
    private const double StackedBelowWidth = 860;

    private LayerListItem? _dragItem;
    private Border? _dragRow;

    public DesignWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
        LayerNameBox.GotFocus += (_, _) => SetEditingName(true);
        LayerNameBox.LostFocus += (_, _) =>
        {
            ViewModel?.Properties.CommitNameCommand.Execute(null);
            SetEditingName(false);
        };
        LayerNameBox.KeyDown += OnNameKeyDown;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
    }

    private DesignWorkspaceViewModel? ViewModel => DataContext as DesignWorkspaceViewModel;

    private void ApplyLayout(double width)
    {
        bool stacked = width < StackedBelowWidth;
        Panes.ColumnDefinitions = stacked ? new ColumnDefinitions("*") : new ColumnDefinitions("*,340");
        Grid.SetColumn(SidePane, stacked ? 0 : 1);
        Grid.SetRow(SidePane, stacked ? 1 : 0);
    }

    private void SetEditingName(bool editing)
    {
        if (ViewModel is { } model)
        {
            model.Properties.IsEditingName = editing;
        }
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } model)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            model.Properties.CommitNameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            model.Properties.RevertNameCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Reorder by dragging a row's handle ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only presses on a row's handle (or the dots inside it) start a reorder.
        Border? grip = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("layer-grip"));
        if (grip?.Tag is not LayerListItem item || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ViewModel?.SelectCommand.Execute(item);
        _dragItem = item;
        _dragRow = grip.FindAncestorOfType<Border>();
        _dragRow?.Classes.Add("dragging");
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null)
        {
            return;
        }

        int target = TargetIndex(e.GetPosition(LayerList));
        foreach (Border row in Rows())
        {
            row.Classes.Set("drop-target", row.DataContext is LayerListItem candidate
                                           && ViewModel is { } model
                                           && model.Layers.Items.IndexOf(candidate) == target
                                           && candidate != _dragItem);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragItem is not { } item)
        {
            return;
        }

        int target = TargetIndex(e.GetPosition(LayerList));
        _dragRow?.Classes.Remove("dragging");
        foreach (Border row in Rows())
        {
            row.Classes.Remove("drop-target");
        }

        _dragItem = null;
        _dragRow = null;
        e.Pointer.Capture(null);
        if (ViewModel is { } model && target != model.Layers.Items.IndexOf(item))
        {
            model.MoveToListIndex(item.Id, target);
        }
    }

    /// <summary>The list row under a point in list coordinates, clamped to the list.</summary>
    private int TargetIndex(Point inList)
    {
        Border[] rows = Rows().ToArray();
        for (int index = 0; index < rows.Length; index++)
        {
            Point? top = rows[index].TranslatePoint(default, LayerList);
            if (top is { } origin && inList.Y < origin.Y + rows[index].Bounds.Height)
            {
                return index;
            }
        }

        return Math.Max(0, rows.Length - 1);
    }

    private IEnumerable<Border> Rows()
        => LayerList.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("layer-row"));
}
