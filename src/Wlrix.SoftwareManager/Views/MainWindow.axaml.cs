using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.ViewModels;
using Wlrix.SoftwareManager.Views.Dialogs;

namespace Wlrix.SoftwareManager.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        viewModel.AboutRequested += OnAboutRequested;
        viewModel.ConflictsRequested += OnConflictsRequested;
        viewModel.RepositoriesRequested += OnRepositoriesRequested;
        viewModel.UserSoftwareRequested += OnUserSoftwareRequested;
        viewModel.CloseRequested += Close;

        // Filling the inventory means running a package manager, so it happens after the window
        // is up rather than in the constructor: `pacman -Qi` over two thousand packages is not
        // something to hold a first paint on.
        viewModel.Reload();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.AboutRequested -= OnAboutRequested;
            viewModel.ConflictsRequested -= OnConflictsRequested;
            viewModel.RepositoriesRequested -= OnRepositoriesRequested;
            viewModel.UserSoftwareRequested -= OnUserSoftwareRequested;
            viewModel.CloseRequested -= Close;
            viewModel.Dispose();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutRequested(string message) => _ = MessageDialog.ShowAsync(
        this, DialogType.Information, message, buttons: DialogButtons.Ok, title: Strings.HelpAbout);

    private void OnConflictsRequested(IReadOnlyList<TransactionConflict> conflicts) =>
        _ = ConflictsDialog.ShowAsync(this, conflicts);

    private void OnRepositoriesRequested()
    {
        // Resolved rather than newed up: the view model needs the backend factory, the
        // transaction service and a logger, which is what the container is for.
        if (App.Services?.GetService(typeof(RepositoriesViewModel)) is RepositoriesViewModel viewModel)
            _ = RepositoriesDialog.ShowAsync(this, viewModel);
    }

    private void OnUserSoftwareRequested()
    {
        if (App.Services?.GetService(typeof(UserSoftwareViewModel)) is UserSoftwareViewModel viewModel)
            _ = UserSoftwareDialog.ShowAsync(this, viewModel);
    }
}
