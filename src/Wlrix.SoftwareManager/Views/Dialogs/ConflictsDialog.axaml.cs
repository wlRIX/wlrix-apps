using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Common;
using Wlrix.Packages.Backends.Parsing;

namespace Wlrix.SoftwareManager.Views.Dialogs;

/// <summary>
/// What the package manager objected to, in its own words. Opens when a transaction fails over a
/// conflict, and again from the Conflicts button, which stays red until the next run.
/// </summary>
public partial class ConflictsDialog : Window
{
    public ConflictsDialog() => InitializeComponent();

    /// <summary>What the package manager refused is a warning, as its own words are.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SystemSound.Play("dialog-warning");
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, IReadOnlyList<TransactionConflict> conflicts)
    {
        var dialog = new ConflictsDialog();
        dialog.FindControl<ItemsControl>("Conflicts")!.ItemsSource = conflicts;
        return dialog.ShowDialog(owner);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
