using Avalonia.Controls;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>
/// Layout of the Preview page: the picture and its settings side by side, stacked when the
/// content gets narrower than the Design page's threshold. All state lives in the view model.
/// </summary>
public partial class PreviewWorkspaceView : UserControl
{
    private const double StackedBelowWidth = 860;

    public PreviewWorkspaceView()
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
