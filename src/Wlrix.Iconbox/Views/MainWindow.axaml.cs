using Avalonia.Controls;
using Avalonia.Input;
using Wlrix.Iconbox.ViewModels;

namespace Wlrix.Iconbox.Views;

public partial class MainWindow : Window
{
    // The tile the left button went down on, so the release only counts on the same one.
    private TileViewModel? _pressed;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    /// <summary>
    /// The right button posts the window's menu at once, as on a 4Dwm frame; the left waits for
    /// its release, as a click on one of the compositor's desktop tiles does.
    /// </summary>
    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TileViewModel tile })
            return;
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            Model?.ShowMenu(tile);
            e.Handled = true;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _pressed = tile;
            e.Handled = true;
        }
    }

    private void OnTileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        var pressed = _pressed;
        _pressed = null;
        // The release goes to the control that took the press, wherever the pointer is now, so
        // a press dragged off the tile is a change of mind rather than a click.
        if (pressed is null || sender is not Control control || control.DataContext != pressed)
            return;
        if (!control.Bounds.WithX(0).WithY(0).Contains(e.GetPosition(control)))
            return;
        Model?.Toggle(pressed);
        e.Handled = true;
    }
}
