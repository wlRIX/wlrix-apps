using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.ViewModels;

namespace Wlrix.Settings.Users.Views;

public abstract class PasswordDialogBase : DialogShell<string?>;

/// <summary>Change Password: the new password twice. Answers it, or null for Cancel.</summary>
public partial class PasswordDialog : PasswordDialogBase
{
    private readonly PasswordModel _model = new();

    public PasswordDialog() : this(string.Empty)
    {
    }

    public PasswordDialog(string account)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
        this.FindControl<TextBlock>("Heading")!.Text = Strings.Catalog.Format("PasswordHeading", account);
        Opened += (_, _) => this.FindControl<TextBox>("First")?.Focus();
        Closed += (_, _) => _model.Clear();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_model.CanFinish)
            Close(_model.Password);
    }
}
