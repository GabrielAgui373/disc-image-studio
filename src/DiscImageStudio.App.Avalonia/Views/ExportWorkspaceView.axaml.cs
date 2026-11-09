using Avalonia.Controls;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>
/// Layout of the Export page: the plan and the destination side by side, stacked when the
/// content gets narrower than the Design page's threshold. All state lives in the view model.
/// </summary>
public partial class ExportWorkspaceView : UserControl
{
    private const double StackedBelowWidth = 860;

    public ExportWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        bool stacked = width < StackedBelowWidth;
        Panes.ColumnDefinitions = stacked ? new ColumnDefinitions("*") : new ColumnDefinitions("*,340");
        Grid.SetColumn(SidePane, stacked ? 0 : 1);
        Grid.SetRow(SidePane, stacked ? 1 : 0);
    }
}
