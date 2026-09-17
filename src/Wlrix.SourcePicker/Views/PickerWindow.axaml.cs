using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.SourcePicker.Controls;
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

    /// <summary>Starts on the first source, so the keyboard lands somewhere useful.</summary>
    /// <remarks>
    /// <para>
    /// Without this nothing has focus when the dialog opens, and the first Tab goes to the
    /// first control in the tree — which is Cancel, because the buttons are declared before the
    /// scroller they are docked below. So Tab then Space canceled the share, which is a poor
    /// thing for the two most obvious keys to do.
    /// </para>
    /// <para>
    /// Posted at <see cref="DispatcherPriority.Loaded"/> because the tiles are generated from a
    /// template and do not exist until the panel has laid them out. The same arrangement the
    /// file picker's listing needs, and for the same reason.
    /// </para>
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(
            () => this.GetVisualDescendants().OfType<LedButton>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

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
