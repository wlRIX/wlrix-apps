using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;
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
        viewModel.TransactionCompleted += OnTransactionCompleted;
        viewModel.TransactionFailed += OnTransactionFailed;
        viewModel.CloseRequested += Close;

        // Filling the inventory means running a package manager, so it happens after the window
        // is up rather than in the constructor: `pacman -Qi` over two thousand packages is not
        // something to hold a first paint on.
        viewModel.Reload();
        _ = viewModel.CheckRestartOnOpenAsync();
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
            viewModel.TransactionCompleted -= OnTransactionCompleted;
            viewModel.TransactionFailed -= OnTransactionFailed;
            viewModel.CloseRequested -= Close;
            viewModel.Dispose();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutRequested()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            title: Strings.AboutTitle, buttons: DialogButtons.Ok, okText: Strings.Ok);
    }

    private void OnConflictsRequested(IReadOnlyList<TransactionConflict> conflicts) =>
        _ = ConflictsDialog.ShowAsync(this, conflicts);

    private async void OnTransactionCompleted(bool needsRestart)
    {
        var restart = App.Services?.GetService(typeof(ISystemRestart)) as ISystemRestart;
        var canRestart = needsRestart && restart is not null
                                      && await restart.CanRestartAsync().ConfigureAwait(true);

        switch (await TransactionCompleteDialog.ShowAsync(this, needsRestart, canRestart).ConfigureAwait(true))
        {
            case TransactionCompleteChoice.Exit:
                Close();
                break;

            case TransactionCompleteChoice.Restart:
                if (await restart!.RestartAsync().ConfigureAwait(true) is { } problem)
                {
                    await MessageDialog.ShowAsync(this, DialogType.Error, problem,
                        buttons: DialogButtons.Ok, title: Strings.SoftwareManager).ConfigureAwait(true);
                }

                break;
        }
    }

    /// <summary>
    /// Says why a transaction failed. A failed download gets its own explanation, because the
    /// fix is something the user can do. After that, the package manager's own objections are the
    /// most useful thing to show, and the last of its errors are what is left.
    /// </summary>
    private void OnTransactionFailed(TransactionFailure failure)
    {
        if (!failure.StaleLists && failure.Conflicts.Count > 0)
        {
            _ = ConflictsDialog.ShowAsync(this, failure.Conflicts);
            return;
        }

        var message = failure.StaleLists ? Strings.FailureStaleLists : failure.Message;
        if (failure.Errors.Count > 0)
            message = $"{message}\n\n{string.Join('\n', failure.Errors)}";

        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, buttons: DialogButtons.Ok,
            title: Strings.FailureTitle);
    }

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
