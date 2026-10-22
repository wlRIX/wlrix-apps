using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Users.Localization;

namespace Wlrix.Settings.Users.Views;

public abstract class DeleteDialogBase : DialogShell<bool?>;

/// <summary>
/// Delete User: are you sure, and the home folder too? Answers whether to remove the files, or
/// null for Cancel. Cancel is the default button: Enter on a dialog about deleting an account
/// should not delete it.
/// </summary>
public partial class DeleteDialog : DeleteDialogBase
{
    public DeleteDialog() : this(string.Empty, string.Empty)
    {
    }

    public DeleteDialog(string account, string home)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("Question")!.Text = Strings.Catalog.Format("DeleteQuestion", account);
        this.FindControl<CheckBox>("Files")!.Content = Strings.Catalog.Format("DeleteFiles", home);
    }

    private void OnDelete(object? sender, RoutedEventArgs e) =>
        Close(this.FindControl<CheckBox>("Files")!.IsChecked == true);
}
