using Avalonia.Controls;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>
/// Layout of the Calibration page: the target and its preview beside the destination, the run
/// and the linked session; stacked when narrow, like the Export page. State lives in the view model.
/// </summary>
public partial class CalibrationWorkspaceView : UserControl
{
    private const double StackedBelowWidth = 860;

    public CalibrationWorkspaceView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => ApplyLayout(args.NewSize.Width);
    }

    private void ApplyLayout(double width)
    {
        bool stacked = width < StackedBelowWidth;
        Panes.ColumnDefinitions = stacked ? new ColumnDefinitions("*") : new ColumnDefinitions("*,340");
        Grid.SetColumn(SidePane, stacked ? 0 : 1);
        Grid.SetRow(SidePane, stacked ? 2 : 1);
    }
}
