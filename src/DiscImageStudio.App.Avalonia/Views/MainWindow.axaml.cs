using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Views;

public partial class MainWindow : Window
{
    private const double CompactLayoutWidth = 900;
    // The compact rail still needs room for the fixed icon column, button padding and focus ring.
    private const double CollapsedSidebarWidth = 72;
    private const double ExpandedSidebarWidth = 240;

    private IApplicationLifetime? _applicationLifetime;
    private bool _closeAllowed;
    private bool _closePending;
    private bool _isSidebarCollapsed;
    private CancellationTokenSource? _sidebarTransitionCancellation;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += (_, _) => ApplyShellLayout();
        SizeChanged += (_, _) => ApplyShellLayout();
    }

    public void AttachLifetime(IApplicationLifetime applicationLifetime)
        => _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));

    private async void ToggleSidebar_Click(object? sender, RoutedEventArgs args)
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        await ApplySidebarToggleAsync();
    }

    private string? SidebarTip(bool isCollapsed)
        => DataContext is DiscImageStudio.App.Avalonia.ViewModels.MainViewModel shell
            ? isCollapsed ? shell.SidebarExpandTip : shell.SidebarCollapseTip
            : null;

    private void ApplyShellLayout()
    {
        bool isCompact = Bounds.Width < CompactLayoutWidth;
        bool isCollapsed = isCompact || _isSidebarCollapsed;

        ApplySidebarState(isCollapsed, showLabels: !isCollapsed);
        HeaderSubtitle.IsVisible = !isCompact;
        SidebarToggleButton.Classes.Set("collapsed", isCollapsed);
        ToolTip.SetTip(SidebarToggleButton, SidebarTip(isCollapsed));
    }

    private async Task ApplySidebarToggleAsync()
    {
        if (Bounds.Width < CompactLayoutWidth)
        {
            ApplyShellLayout();
            return;
        }

        bool isCollapsed = _isSidebarCollapsed;
        _sidebarTransitionCancellation?.Cancel();
        _sidebarTransitionCancellation?.Dispose();
        _sidebarTransitionCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _sidebarTransitionCancellation.Token;

        HeaderSubtitle.IsVisible = true;
        SidebarToggleButton.Classes.Set("collapsed", isCollapsed);
        ToolTip.SetTip(SidebarToggleButton, SidebarTip(isCollapsed));

        try
        {
            if (isCollapsed)
            {
                SetSidebarLabelOpacity(0);
                await Task.Delay(TimeSpan.FromMilliseconds(140), cancellationToken);
                ApplySidebarState(isCollapsed: true, showLabels: false);
            }
            else
            {
                ApplySidebarState(isCollapsed: false, showLabels: false);
                await Task.Delay(TimeSpan.FromMilliseconds(220), cancellationToken);
                SetSidebarLabelOpacity(1);
            }
        }
        catch (OperationCanceledException)
        {
            // A new toggle superseded this purely visual transition.
        }
    }

    private void ApplySidebarState(bool isCollapsed, bool showLabels)
    {
        Sidebar.Width = isCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;
        Sidebar.Classes.Set("sidebar-collapsed", isCollapsed);
        SetSidebarLabelOpacity(showLabels ? 1 : 0);
    }

    private void SetSidebarLabelOpacity(double opacity)
    {
        foreach (TextBlock label in Sidebar.GetVisualDescendants()
                     .OfType<TextBlock>()
                     .Where(text => text.Classes.Contains("sidebar-label")))
        {
            label.Opacity = opacity;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closeAllowed || _applicationLifetime is null)
        {
            return;
        }

        args.Cancel = true;
        if (_closePending)
        {
            return;
        }

        _closePending = true;
        try
        {
            if (await _applicationLifetime.RequestShutdownAsync())
            {
                _closeAllowed = true;
                Close();
            }
        }
        finally
        {
            _closePending = false;
        }
    }
}
