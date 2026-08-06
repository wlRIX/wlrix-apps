using Avalonia.Controls;
using Avalonia.Interactivity;
using Wlrix.SourcePicker.ViewModels;

namespace Wlrix.SourcePicker.Views;

public partial class PickerWindow : Window
{
    public PickerWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// What the user chose, or null if they did not choose.
    /// </summary>
    /// <remarks>
    /// Null is the default and stays null unless Share is pressed, so every other way out of
    /// this dialog -- Cancel, Escape, the window's close button, the compositor taking it away
    /// -- is a cancel without needing to be handled one by one.
    /// </remarks>
    public IReadOnlyList<string>? Result { get; private set; }

    private void OnShare(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PickerViewModel model || model.SelectionCount == 0)
        {
            return;
        }

        Result = model.Chosen;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
