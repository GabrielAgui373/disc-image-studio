using DiscImageStudio.App.Avalonia.Controls;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class AvaloniaDesignControlTests
{
    [Fact]
    public void Base_design_controls_expose_reusable_state_without_application_singletons()
    {
        PageHeader header = new()
        {
            Title = "Design",
            Subtitle = "Arrange source layers.",
            Badge = "DVD",
            IsBadgeVisible = true,
        };
        SectionCard card = new()
        {
            Title = "Source",
            Description = "The files used by this workflow.",
            Content = "body",
        };
        SourceChip chip = new()
        {
            Text = "Artwork",
            Detail = "cover.png",
        };
        PathField path = new()
        {
            Label = "Destination",
            Text = "/tmp/disc.iso",
            Watermark = "/home/user/Disc Images",
            Hint = "The destination is selected in the next workflow step.",
            IsReadOnly = true,
        };
        EmptyState empty = new()
        {
            Title = "No project open",
            Description = "Create or open a project to continue.",
            ActionText = "Create project",
            IsActionVisible = true,
        };

        Assert.True(header.IsBadgeVisible);
        Assert.Equal("DVD", header.Badge);
        Assert.Equal("body", card.Content);
        Assert.Equal("cover.png", chip.Detail);
        Assert.Equal("/tmp/disc.iso", path.Text);
        Assert.True(path.IsReadOnly);
        Assert.True(empty.IsActionVisible);
    }
}
