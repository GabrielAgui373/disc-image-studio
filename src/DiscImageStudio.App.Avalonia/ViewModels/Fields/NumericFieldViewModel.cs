using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiscImageStudio.App.Avalonia.ViewModels.Fields;

/// <summary>
/// A numeric property field with an edit buffer. Typing only changes <see cref="Text"/>; the
/// value is parsed, validated and handed to the commit callback on Enter or focus loss
/// (<see cref="CommitCommand"/>), so a half-typed value never overwrites valid project state and
/// each committed value is one history step. Escape (<see cref="RevertCommand"/>) restores the
/// committed value. While the user edits, external updates (<see cref="Show"/>) are held back.
/// </summary>
public sealed partial class NumericFieldViewModel : ObservableObject
{
    private readonly Func<double, bool> _commit;
    private readonly string _rangeMessage;
    private readonly string _rejectedMessage;
    private readonly string _format;

    /// <param name="commit">Applies a valid value; returns false when the project rejected it.</param>
    /// <param name="rangeMessageFormat">Message with {0} = minimum and {1} = maximum.</param>
    public NumericFieldViewModel(
        string label,
        string unit,
        double minimum,
        double maximum,
        int decimals,
        Func<double, bool> commit,
        string rangeMessageFormat,
        string rejectedMessage)
    {
        if (!(minimum <= maximum))
        {
            throw new ArgumentException("The minimum must not exceed the maximum.", nameof(minimum));
        }

        Label = label;
        Unit = unit;
        Minimum = minimum;
        Maximum = maximum;
        Decimals = Math.Clamp(decimals, 0, 6);
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _format = "F" + Decimals.ToString(CultureInfo.InvariantCulture);
        _rangeMessage = string.Format(CultureInfo.CurrentUICulture, rangeMessageFormat, Format(minimum), Format(maximum));
        _rejectedMessage = rejectedMessage;
    }

    public string Label { get; }

    public string Unit { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public int Decimals { get; }

    /// <summary>The last committed (or externally shown) value.</summary>
    public double Value { get; private set; }

    /// <summary>Set by the view while the field has keyboard focus.</summary>
    public bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Shows a value coming from the project (selection change, undo, canvas drag).</summary>
    public void Show(double value)
    {
        Value = value;
        if (!IsEditing)
        {
            Text = Format(value);
            Error = null;
        }
    }

    [RelayCommand]
    private void Commit()
    {
        if (!TryParse(Text, out double value) || value < Minimum || value > Maximum)
        {
            Error = _rangeMessage;
            return;
        }

        double rounded = Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - Value) < Math.Pow(10, -Decimals) / 2)
        {
            // Unchanged: tidy the text without creating a history step.
            Text = Format(Value);
            Error = null;
            return;
        }

        if (!_commit(rounded))
        {
            Error = _rejectedMessage;
            return;
        }

        Value = rounded;
        Text = Format(rounded);
        Error = null;
    }

    [RelayCommand]
    private void Revert()
    {
        Text = Format(Value);
        Error = null;
    }

    private string Format(double value) => value.ToString(_format, CultureInfo.CurrentUICulture);

    /// <summary>Accepts the interface language's decimal separator and, as a fallback, the invariant one.</summary>
    private static bool TryParse(string? text, out double value)
    {
        string trimmed = (text ?? string.Empty).Trim();
        return (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentUICulture, out value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
               && double.IsFinite(value);
    }
}
