using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Users.ViewModels;

namespace Wlrix.Settings.Users.Views;

/// <summary>What Add User came back with.</summary>
public sealed record NewAccount(string UserName, string RealName, bool Admin, string Password);

public abstract class AddUserDialogBase : DialogShell<NewAccount?>;

/// <summary>Add User. Checks the login name is free before answering, so a clash is said here.</summary>
public partial class AddUserDialog : AddUserDialogBase
{
    private readonly AddUserModel _model = new();
    private readonly Func<string, Task<bool>> _taken;

    public AddUserDialog() : this(_ => Task.FromResult(false))
    {
    }

    public AddUserDialog(Func<string, Task<bool>> taken)
    {
        _taken = taken;
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
        Opened += (_, _) => this.FindControl<TextBox>("First")?.Focus();
        Closed += (_, _) => _model.Clear();
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanFinish)
            return;
        if (await _taken(_model.UserName))
        {
            _model.Taken = true;
            return;
        }
        Close(new NewAccount(_model.UserName, _model.FullName.Trim(), _model.AccountType == 1, _model.Password));
    }
}
