using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Bluetooth.Localization;
using Wlrix.Settings.Bluetooth.ViewModels;

namespace Wlrix.Settings.Bluetooth.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private BluetoothSettingsViewModel? Model => DataContext as BluetoothSettingsViewModel;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnPair(object? sender, RoutedEventArgs e) => _ = Model?.PairAsync();

    private void OnConnect(object? sender, RoutedEventArgs e) => _ = Model?.ConnectAsync();

    private void OnDisconnect(object? sender, RoutedEventArgs e) => _ = Model?.DisconnectAsync();

    /// <summary>
    /// Forgetting is the one thing here that cannot be undone from this window -- the device has to
    /// be put back into pairing mode and paired again -- so it is asked about first.
    /// </summary>
    private async void OnForget(object? sender, RoutedEventArgs e)
    {
        if (Model is not { Selected: { } device } model)
            return;
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question,
            Strings.Catalog.Format("ForgetQuestion", device.Name),
            okText: Strings.Catalog.Get("ForgetConfirm"));
        if (answer == DialogResult.Ok)
            await model.ForgetAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
