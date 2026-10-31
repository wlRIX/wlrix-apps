using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Common;

namespace Wlrix.SoftwareManager.Views.Dialogs;

/// <summary>What the user chose to do once a transaction had finished.</summary>
public enum TransactionCompleteChoice
{
    Continue,
    Exit,
    Restart,
}

/// <summary>
/// The IRIX question after installing or removing software: carry on, leave, or restart.
/// The restart line appears only when a restart is needed, and the restart choice only when this
/// session is also allowed to restart. Closing the window any other way means carry on.
/// </summary>
public partial class TransactionCompleteDialog : Window
{
    private TransactionCompleteChoice _choice = TransactionCompleteChoice.Continue;

    public TransactionCompleteDialog() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SystemSound.Play("dialog-question");
    }

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns the choice.</summary>
    public static async Task<TransactionCompleteChoice> ShowAsync(Window owner, bool needsRestart, bool canRestart)
    {
        var dialog = new TransactionCompleteDialog();
        dialog.FindControl<TextBlock>("RestartLine")!.IsVisible = needsRestart;
        dialog.FindControl<RadioButton>("RestartChoice")!.IsVisible = needsRestart && canRestart;

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return dialog._choice;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<RadioButton>("ExitChoice")!.IsChecked == true)
            _choice = TransactionCompleteChoice.Exit;
        else if (this.FindControl<RadioButton>("RestartChoice")!.IsChecked == true)
            _choice = TransactionCompleteChoice.Restart;

        Close();
    }
}
