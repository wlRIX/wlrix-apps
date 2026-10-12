// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.ImageView.Localization;
using Wlrix.ImageView.ViewModels;
using Wlrix.ImageView.Views;
using Wlrix.Theme;

namespace Wlrix.ImageView;

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
        // Draw in the session's color scheme, and follow it when it changes. Not awaited: a
        // window's first paint does not wait on a color.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("imageview");
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            var window = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>(),
            };

            // An image named on the command line opens straight away, so the viewer can be the
            // handler for one from a file manager.
            if (desktop.Args is [var path, ..] && File.Exists(path))
                window.OpenOnStartup(path);

            desktop.MainWindow = window;

            // Dispose the provider on shutdown so ZLogger flushes its buffered output.
            desktop.ShutdownRequested += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
