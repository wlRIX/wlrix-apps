using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.ViewModels;

namespace Wlrix.Toolchest.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.ShowAbout += OnShowAbout;
        vm.ShowError += OnShowError;
        vm.ConfirmLogOut += OnConfirmLogOut;
        _ = vm.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ShowAbout -= OnShowAbout;
            vm.ShowError -= OnShowError;
            vm.ConfirmLogOut -= OnConfirmLogOut;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnShowAbout(string message) => _ = MessageDialog.ShowAsync(this, DialogType.Information,
        message, buttons: DialogButtons.Ok, title: Strings.AboutToolchest);

    private void OnShowError(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, buttons: DialogButtons.Ok);

    /// <summary>Whether the user confirmed ending the session. Dismissed (no result) is a no.</summary>
    private async Task<bool> OnConfirmLogOut()
    {
        var result = await MessageDialog.ShowAsync(this, DialogType.Question, Strings.LogOutPrompt,
            buttons: DialogButtons.OkCancel, title: Strings.LogOut, okText: Strings.LogOut);
        return result == DialogResult.Ok;
    }
}
