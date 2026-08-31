using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.Shutdown.Localization;
using Wlrix.Shutdown.Services;
using Wlrix.Shutdown.ViewModels;
using Wlrix.Shutdown.Views;

namespace Wlrix.Shutdown;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("shutdown");
            services.AddSingleton<IPowerService, LogindPowerService>();
            _services = services.BuildServiceProvider();

            desktop.MainWindow = new ShutdownWindow
            {
                DataContext = new ShutdownViewModel(
                    _services.GetRequiredService<IPowerService>(),
                    _services.GetRequiredService<ILogger<ShutdownViewModel>>(),
                    Program.StartWithRestart)
            };

            // Dispose the provider on shutdown so ZLogger flushes its buffered output, and the
            // system bus connection goes with it.
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
