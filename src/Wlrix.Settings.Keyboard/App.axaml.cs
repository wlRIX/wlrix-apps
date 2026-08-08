using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Settings.Keyboard.ViewModels;
using Wlrix.Settings.Keyboard.Views;

namespace Wlrix.Settings.Keyboard;

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
            var model = new KeyboardSettingsViewModel();
            desktop.MainWindow = new MainWindow { DataContext = model };

            // The window is shown first and filled in when the daemon answers. The constructor
            // used to read the config file synchronously; asking a bus for it is not something
            // to block a window's first paint on, and bus activation means the very first call
            // may also have to start the daemon.
            _ = model.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
