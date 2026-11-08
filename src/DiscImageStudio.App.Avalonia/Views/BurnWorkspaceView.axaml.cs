using Avalonia.Controls;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>Layout of the Burn page: what and where beside the checks and the burn; stacked when narrow.</summary>
public partial class BurnWorkspaceView : UserControl
{
    private const double StackedBelowWidth = 860;

    public BurnWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            bool stacked = args.NewSize.Width < StackedBelowWidth;
            Panes.ColumnDefinitions = stacked ? new ColumnDefinitions("*") : new ColumnDefinitions("*,340");
            Grid.SetColumn(SidePane, stacked ? 0 : 1);
            Grid.SetRow(SidePane, stacked ? 1 : 0);
        };
    }
}
