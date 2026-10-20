using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.Bluetooth.Localization;
using Wlrix.Settings.Bluetooth.ViewModels;
using Wlrix.Settings.Bluetooth.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.Bluetooth;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new BluetoothSettingsViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };
            desktop.Exit += (_, _) => model.Dispose();
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
