using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.SoftwareManager.ViewModels;

namespace Wlrix.SoftwareManager.Views.Dialogs;

/// <summary>The configured software sources, and what can be done to them.</summary>
public partial class RepositoriesDialog : Window
{
    public RepositoriesDialog() => InitializeComponent();

    /// <summary>Shows the dialog over <paramref name="owner"/>, filled in once it is up.</summary>
    public static Task ShowAsync(Window owner, RepositoriesViewModel viewModel)
    {
        var dialog = new RepositoriesDialog { DataContext = viewModel };
        return dialog.ShowDialog(owner);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Reading the sources means running the package manager, so it happens after the window
        // is up rather than in the constructor.
        if (DataContext is RepositoriesViewModel viewModel)
            _ = viewModel.LoadAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
