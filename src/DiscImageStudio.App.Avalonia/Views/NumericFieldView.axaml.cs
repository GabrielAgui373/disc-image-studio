using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiscImageStudio.App.Avalonia.ViewModels.Fields;

namespace DiscImageStudio.App.Avalonia.Views;

/// <summary>
/// Hosts a <see cref="NumericFieldViewModel"/>: focus marks the field as being edited, Enter or
/// focus loss commits, Escape reverts. The view model owns parsing, validation and history.
/// </summary>
public partial class NumericFieldView : UserControl
{
    public NumericFieldView()
    {
        InitializeComponent();
        Input.GotFocus += (_, _) => SetEditing(true);
        Input.LostFocus += OnLostFocus;
        Input.KeyDown += OnKeyDown;
    }

    private NumericFieldViewModel? Field => DataContext as NumericFieldViewModel;

    private void SetEditing(bool editing)
    {
        if (Field is { } field)
        {
            field.IsEditing = editing;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        Field?.CommitCommand.Execute(null);
        SetEditing(false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Field is not { } field)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            field.CommitCommand.Execute(null);
            Input.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            field.RevertCommand.Execute(null);
            e.Handled = true;
        }
    }
}
