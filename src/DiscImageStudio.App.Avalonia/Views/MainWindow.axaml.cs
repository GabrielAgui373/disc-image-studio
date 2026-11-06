using Avalonia.Controls;
using DiscImageStudio.Application.Ports;

namespace DiscImageStudio.App.Avalonia.Views;

public partial class MainWindow : Window
{
    private IApplicationLifetime? _applicationLifetime;
    private bool _closeAllowed;
    private bool _closePending;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void AttachLifetime(IApplicationLifetime applicationLifetime)
        => _applicationLifetime = applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));

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
