using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Packages.Backends.Parsing;

namespace Wlrix.SoftwareManager.Views.Dialogs;

/// <summary>
/// What the package manager objected to, in its own words. Opened from the Conflicts button,
/// which turns red once a transaction has reported one.
/// </summary>
public partial class ConflictsDialog : Window
{
    public ConflictsDialog() => InitializeComponent();

    /// <summary>Shows the dialog over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, IReadOnlyList<TransactionConflict> conflicts)
    {
        var dialog = new ConflictsDialog();
        dialog.Conflicts.ItemsSource = conflicts;
        return dialog.ShowDialog(owner);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
