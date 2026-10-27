using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Wlrix.Desks.Models;

namespace Wlrix.Desks.Views;

/// <summary>
/// The Desk menu's Scaling › Custom...: a slider from 4% to 18% for a snapshot scale the
/// presets do not offer.
/// </summary>
/// <remarks>
/// Apply sets the scale and leaves the dialog up, OK sets it and closes, and Cancel only
/// closes: as in any Motif dialog with an Apply button, Cancel does not take back what Apply
/// already did.
/// </remarks>
public partial class CustomScaleDialog : Window
{
    private Action<int>? _apply;

    public CustomScaleDialog()
    {
        InitializeComponent();
        ScaleSlider.Minimum = DeskScale.Minimum;
        ScaleSlider.Maximum = DeskScale.Maximum;
        ScaleSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                ValueLabel.Text = Percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        };
        LayoutUpdated += (_, _) => PlaceValueLabel();

        Accept.Click += (_, _) =>
        {
            _apply?.Invoke(Percent);
            Close();
        };
        Apply.Click += (_, _) => _apply?.Invoke(Percent);
        Reject.Click += (_, _) => Close();
    }

    private int Percent => (int)Math.Round(ScaleSlider.Value);

    /// <summary>Opens the dialog with the slider at <paramref name="current"/>.</summary>
    /// <param name="owner">The window the dialog is modal to.</param>
    /// <param name="current">The scale in use, where the slider starts.</param>
    /// <param name="apply">Sets the user-defined scale; called by OK and by each Apply.</param>
    public static Task ShowAsync(Window owner, int current, Action<int> apply)
    {
        var dialog = new CustomScaleDialog { _apply = apply };
        dialog.ScaleSlider.Value = Math.Clamp(current, DeskScale.Minimum, DeskScale.Maximum);
        dialog.ValueLabel.Text = dialog.Percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
        dialog.Opened += (_, _) => dialog.ScaleSlider.Focus();
        return dialog.ShowDialog(owner);
    }

    // Centers the amount over the thumb. Runs after every layout pass, because the thumb moves
    // by being arranged and the label changes width between 9 and 10; setting the position it
    // already has does not invalidate anything, so this settles rather than looping.
    private void PlaceValueLabel()
    {
        if (ScaleSlider.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is not { } thumb
            || thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, 0), ValueTrack) is not { } center)
            return;

        var left = Math.Round(center.X - ValueLabel.Bounds.Width / 2);
        if (Canvas.GetLeft(ValueLabel) != left)
            Canvas.SetLeft(ValueLabel, left);
    }
}
