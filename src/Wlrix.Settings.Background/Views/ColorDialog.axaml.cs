using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Wlrix.Settings.Background.Views;

/// <summary>The desktop color, chosen in IRIX's Color Browser. Closes with the color, or null.</summary>
public partial class ColorDialog : Window
{
    public ColorDialog() => InitializeComponent();

    public ColorDialog(Color color) : this()
    {
        Chooser.Color = color;
        // The stored swatch starts as the color in force, so Recall is "what I had".
        Chooser.StoredColor = color;
    }

    private void OnOk(object? sender, RoutedEventArgs e) =>
        // Opaque whatever the chooser says: the desktop color has no alpha, because there is
        // nothing behind the background to show through.
        Close((Color?)Color.FromRgb(Chooser.Color.R, Chooser.Color.G, Chooser.Color.B));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
