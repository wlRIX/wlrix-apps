using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Network.Localization;
using Wlrix.Settings.Network.ViewModels;

namespace Wlrix.Settings.Network.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private NetworkSettingsViewModel? Model => DataContext as NetworkSettingsViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnConnect(object? sender, RoutedEventArgs e) => _ = Model?.ConnectAsync();

    private void OnChangePassword(object? sender, RoutedEventArgs e) => _ = Model?.ChangePasswordAsync();

    /// <summary>
    /// Forgetting is the one thing here that cannot be undone from this window -- the password goes
    /// with it -- so it is asked about first.
    /// </summary>
    private async void OnForget(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Selected: { } network } model)
            return;
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question,
            Strings.Catalog.Format("ForgetQuestion", network.Name),
            okText: Strings.Catalog.Get("ForgetConfirm"));
        if (answer == DialogResult.Ok)
            await model.ForgetAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
