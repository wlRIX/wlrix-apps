using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.ViewModels;

namespace Wlrix.Settings.Users.Views;

public partial class MainWindow : Window
{
    private AccountRow? _shown;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private UsersViewModel? Model => DataContext as UsersViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } model)
        {
            model.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(UsersViewModel.Selected))
                    await OnSelectionChangingAsync(model);
            };
        }
    }

    /// <summary>
    /// Leaving an account with edits not yet applied: ask, rather than drop them silently or
    /// carry them over to the next one.
    /// </summary>
    private async Task OnSelectionChangingAsync(UsersViewModel model)
    {
        var leaving = _shown;
        _shown = model.Selected;
        if (leaving is null || leaving == model.Selected || !model.PendingFor(leaving))
            return;
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question,
            Strings.Catalog.Format("ApplyQuestion", leaving.DisplayName),
            okText: Strings.Catalog.Get("ApplyConfirm"), cancelText: Strings.Catalog.Get("Discard"));
        if (answer == DialogResult.Ok)
            await model.ApplyPendingAsync();
        else
            model.DropPending();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) => Model?.Reset();

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            await model.ApplyAsync();
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var account = await new AddUserDialog(model.UserNameTakenAsync).ShowDialog<NewAccount?>(this);
        if (account is not null)
            await model.CreateAsync(account.UserName, account.RealName, account.Admin, account.Password);
    }

    private async void OnPicture(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        var choice = await new PictureDialog().ShowDialog<PictureChoice?>(this);
        if (choice is not null)
            await model.SetPictureAsync(choice);
    }

    private async void OnPassword(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Selected: { } row } model)
            return;
        var password = await new PasswordDialog(row.DisplayName).ShowDialog<string?>(this);
        if (password is not null)
            await model.SetPasswordAsync(password);
    }

    private async void OnFingerprints(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Selected: { } row })
            return;
        await new FingerprintDialog(row.DisplayName).ShowDialog<object?>(this);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Selected: { } row } model)
            return;
        var home = row.Info.HomeDirectory;
        var removeFiles = await new DeleteDialog(row.DisplayName, home).ShowDialog<bool?>(this);
        if (removeFiles is { } remove)
            await model.DeleteAsync(remove);
    }
}
