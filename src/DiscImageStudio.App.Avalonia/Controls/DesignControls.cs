using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace DiscImageStudio.App.Avalonia.Controls;

/// <summary>
/// A shared page heading with an optional family badge and an action slot.
/// </summary>
public sealed class PageHeader : TemplatedControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<string> BadgeProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Badge), string.Empty);

    public static readonly StyledProperty<bool> IsBadgeVisibleProperty =
        AvaloniaProperty.Register<PageHeader, bool>(nameof(IsBadgeVisible));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string Badge
    {
        get => GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public bool IsBadgeVisible
    {
        get => GetValue(IsBadgeVisibleProperty);
        set => SetValue(IsBadgeVisibleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>
/// A titled surface used to group related page content.
/// </summary>
public sealed class SectionCard : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SectionCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<SectionCard, string>(nameof(Description), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>
/// A concise, read-only presentation of the source used by a workflow.
/// </summary>
public sealed class SourceChip : TemplatedControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<SourceChip, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<string> DetailProperty =
        AvaloniaProperty.Register<SourceChip, string>(nameof(Detail), string.Empty);

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SourceChip, object?>(nameof(Actions));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>
/// A consistent label, path input and optional browse action.
/// </summary>
public sealed class PathField : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<PathField, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<PathField, string?>(nameof(Text));

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<PathField, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<PathField, string>(nameof(Hint), string.Empty);

    public static readonly StyledProperty<string> BrowseTextProperty =
        AvaloniaProperty.Register<PathField, string>(nameof(BrowseText), "Browse");

    public static readonly StyledProperty<ICommand?> BrowseCommandProperty =
        AvaloniaProperty.Register<PathField, ICommand?>(nameof(BrowseCommand));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<PathField, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> ShowBrowseProperty =
        AvaloniaProperty.Register<PathField, bool>(nameof(ShowBrowse));

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string BrowseText
    {
        get => GetValue(BrowseTextProperty);
        set => SetValue(BrowseTextProperty, value);
    }

    public ICommand? BrowseCommand
    {
        get => GetValue(BrowseCommandProperty);
        set => SetValue(BrowseCommandProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool ShowBrowse
    {
        get => GetValue(ShowBrowseProperty);
        set => SetValue(ShowBrowseProperty, value);
    }
}

/// <summary>
/// Reusable in-place guidance for an empty functional area.
/// </summary>
public sealed class EmptyState : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<string> ActionTextProperty =
        AvaloniaProperty.Register<EmptyState, string>(nameof(ActionText), string.Empty);

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(ActionCommand));

    public static readonly StyledProperty<bool> IsActionVisibleProperty =
        AvaloniaProperty.Register<EmptyState, bool>(nameof(IsActionVisible));

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string ActionText
    {
        get => GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public bool IsActionVisible
    {
        get => GetValue(IsActionVisibleProperty);
        set => SetValue(IsActionVisibleProperty, value);
    }
}

/// <summary>
/// Vector artwork of an optical disc drawn to real 120 mm proportions (center hole, clamping
/// area, mirror band, data area and rim). <see cref="Variant"/> selects the recording dye look:
/// <c>cd</c> silver, <c>dvd</c> violet and <c>bd</c> deep blue. The artwork scales to any size.
/// An optional <see cref="Artwork"/> is printed on the data side, between the clamping area and
/// the rim, so a project's picture reads as a real disc rather than a cropped circle.
/// </summary>
public sealed class DiscArt : TemplatedControl
{
    public static readonly StyledProperty<string> VariantProperty =
        AvaloniaProperty.Register<DiscArt, string>(nameof(Variant), "cd");

    public static readonly StyledProperty<IImage?> ArtworkProperty =
        AvaloniaProperty.Register<DiscArt, IImage?>(nameof(Artwork));

    private Path? _artworkPart;

    public string Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public IImage? Artwork
    {
        get => GetValue(ArtworkProperty);
        set => SetValue(ArtworkProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _artworkPart = e.NameScope.Find<Path>("PART_Artwork");
        ApplyArtwork();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ArtworkProperty)
        {
            ApplyArtwork();
        }
    }

    private void ApplyArtwork()
    {
        if (_artworkPart is null)
        {
            return;
        }

        // The picture spans the whole disc face; the path's hole keeps the hub area clear.
        _artworkPart.Fill = Artwork is IImageBrushSource source
            ? new ImageBrush(source) { Stretch = Stretch.UniformToFill }
            : null;
        _artworkPart.IsVisible = _artworkPart.Fill is not null;
    }
}
