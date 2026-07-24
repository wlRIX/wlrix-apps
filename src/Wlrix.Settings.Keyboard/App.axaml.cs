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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new KeyboardSettingsViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
