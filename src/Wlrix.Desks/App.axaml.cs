using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Desks.Services;
using Wlrix.Desks.ViewModels;
using Wlrix.Desks.Views;

namespace Wlrix.Desks;

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
            // The live feed talks the wlrix-desks Wayland protocol; `--demo` runs the
            // fabricated sample layout instead, so the UI works without a compositor.
            var demo = desktop.Args?.Contains("--demo") == true;
            IDeskFeed feed = demo ? new SampleDeskFeed() : new WaylandDeskFeed();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(feed)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
