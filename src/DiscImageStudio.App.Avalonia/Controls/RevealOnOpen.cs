using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DiscImageStudio.App.Avalonia.Controls;

/// <summary>
/// Scrolls the nearest <see cref="ScrollViewer"/> so content revealed by an <see cref="Expander"/>
/// or a <see cref="ComboBox"/> drop-down is visible instead of opening below the fold of a page
/// or modal. Enabled for every Expander and ComboBox through the shared control styles.
/// </summary>
public static class RevealOnOpen
{
    // Fluent's Expander animates its content in; reveal again once the animation has settled.
    private static readonly TimeSpan ExpanderSettleDelay = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromMilliseconds(40);
    private static readonly HashSet<ComboBox> Reopening = [];
    private const double EstimatedItemHeight = 56;
    private const double DropDownChrome = 12;
    private const double ViewportMargin = 12;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(RevealOnOpen));

    static RevealOnOpen()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        bool enabled = args.GetNewValue<bool>();
        switch (control)
        {
            case Expander expander:
                expander.PropertyChanged -= OnExpanderPropertyChanged;
                if (enabled)
                {
                    expander.PropertyChanged += OnExpanderPropertyChanged;
                }

                break;
            case ComboBox comboBox:
                comboBox.DropDownOpened -= OnDropDownOpened;
                if (enabled)
                {
                    comboBox.DropDownOpened += OnDropDownOpened;
                }

                break;
        }
    }

    private static void OnExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is not Expander expander
            || args.Property != Expander.IsExpandedProperty
            || !args.GetNewValue<bool>())
        {
            return;
        }

        Dispatcher.UIThread.Post(() => RevealExpander(expander), DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(() => RevealExpander(expander), ExpanderSettleDelay);
    }

    private static void RevealExpander(Expander expander)
    {
        if (!expander.IsExpanded || expander.FindAncestorOfType<ScrollViewer>() is not { } scrollViewer)
        {
            return;
        }

        // Reveal the bottom of the expander, but never push its header above the viewport.
        double? top = expander.TranslatePoint(default, scrollViewer)?.Y;
        if (top is null)
        {
            return;
        }

        double bottom = top.Value + expander.Bounds.Height;
        double overflow = bottom + ViewportMargin - scrollViewer.Viewport.Height;
        double shift = Math.Min(overflow, top.Value - ViewportMargin);
        ScrollBy(scrollViewer, shift);
    }

    private static void OnDropDownOpened(object? sender, EventArgs args)
    {
        if (sender is not ComboBox comboBox
            || comboBox.FindAncestorOfType<ScrollViewer>() is not { } scrollViewer)
        {
            return;
        }

        // A reopen issued below is already anchored to the scrolled position.
        if (Reopening.Remove(comboBox))
        {
            return;
        }

        double? top = comboBox.TranslatePoint(default, scrollViewer)?.Y;
        if (top is null)
        {
            return;
        }

        double dropDownHeight = Math.Min(
            comboBox.MaxDropDownHeight,
            (Math.Max(comboBox.ItemCount, 1) * EstimatedItemHeight) + DropDownChrome);
        double bottom = top.Value + comboBox.Bounds.Height + dropDownHeight;
        double overflow = bottom + ViewportMargin - scrollViewer.Viewport.Height;
        double shift = Math.Min(overflow, top.Value - ViewportMargin);
        if (!ScrollBy(scrollViewer, shift))
        {
            return;
        }

        // The drop-down was placed before the scroll and Popup has no public re-anchor API, so it
        // is reopened once the scrolled layout has been arranged and rendered.
        comboBox.IsDropDownOpen = false;
        DispatcherTimer.RunOnce(
            () =>
            {
                if (comboBox.IsEffectivelyVisible && comboBox.IsEnabled)
                {
                    Reopening.Add(comboBox);
                    comboBox.IsDropDownOpen = true;
                }
            },
            ReopenDelay);
    }

    private static bool ScrollBy(ScrollViewer scrollViewer, double shift)
    {
        double maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        double target = Math.Clamp(scrollViewer.Offset.Y + shift, 0, maximum);
        if (shift <= 0.5 || Math.Abs(target - scrollViewer.Offset.Y) < 0.5)
        {
            return false;
        }

        scrollViewer.Offset = scrollViewer.Offset.WithY(target);
        return true;
    }
}
