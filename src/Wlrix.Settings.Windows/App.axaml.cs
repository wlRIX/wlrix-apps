using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Windows.ViewModels;
using Wlrix.Settings.Windows.Views;

namespace Wlrix.Settings.Windows;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new WindowSettingsViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // The window is shown first and filled in when the daemon answers, as in
            // Wlrix.Settings.Keyboard: asking a bus for the settings is not something to block a
            // window's first paint on, and bus activation means the very first call may also
            // have to start the daemon.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
