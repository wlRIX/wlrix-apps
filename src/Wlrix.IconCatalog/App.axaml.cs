// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Common.Desktop;
using Wlrix.Common.Localization;
using Wlrix.IconCatalog.Localization;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Rendering;
using Wlrix.IconCatalog.Services;
using Wlrix.IconCatalog.ViewModels;
using Wlrix.IconCatalog.Views;
using Wlrix.Icons;
using Wlrix.Theme;
using ZLogger;

namespace Wlrix.IconCatalog;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme. Not awaited, and nothing fails without a settings
        // service.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("icon-catalog");
            services.AddSingleton(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<CatalogState>>();
                return new CatalogState(warn: message => logger.ZLogWarning($"{message}"));
            });
            // The desktop's icon theme: wlrix, which inherits Adwaita, so a launcher whose icon it
            // does not draw still finds the one a GTK application would have shown.
            services.AddSingleton(_ => new IconLoader(new XdgIconTheme()));
            services.AddSingleton<CarpetRenderer>();
            services.AddSingleton<IconImages>();
            services.AddSingleton<RunningApps>();
            services.AddSingleton<Launcher>();
            services.AddSingleton(_ => new ApplicationWatcher(DesktopEntryScanner.ApplicationDirectories()));
            services.AddSingleton<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            // The carpets are tinted by the scheme's icon colors, so those are read before the
            // first page is drawn and again whenever SessionScheme swaps the scheme.
            var images = _services.GetRequiredService<IconImages>();
            var model = _services.GetRequiredService<MainWindowViewModel>();
            images.SetColors(IconStateColorsFromTheme());
            ResourcesChanged += (_, _) =>
            {
                if (images.SetColors(IconStateColorsFromTheme()))
                    model.Redraw();
            };

            var state = _services.GetRequiredService<CatalogState>().Document;
            var window = new MainWindow { DataContext = model };
            if (state.WindowWidth is > 0 and var width && state.WindowHeight is > 0 and var height)
            {
                window.Width = width;
                window.Height = height;
            }
            desktop.MainWindow = window;

            // Another launch, from the toolchest or anywhere else. Arrives on a bus thread.
            if (Program.Guard is { } guard)
                guard.OpenRequested += _ => Dispatcher.UIThread.Post(window.Activate);

            desktop.ShutdownRequested += (_, _) =>
            {
                model.Dispose();
                Program.Guard?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                // Disposed last, so ZLogger flushes what the rest logged on the way out.
                _services.Dispose();
            };

            _ = model.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The current scheme's icon colors, or the classic scheme's if it has none.</summary>
    private IconStateColors IconStateColorsFromTheme()
    {
        var fallback = IconStateColors.Default;
        return new IconStateColors(
            Read("WlrixIconTintColor", fallback.Rest),
            Read("WlrixIconTintHoverColor", fallback.Hover),
            Read("WlrixIconTintSelectedColor", fallback.Selected),
            Read("WlrixIconTintGhostColor", fallback.Ghost),
            Read("WlrixIconTintDropColor", fallback.Drop));

        uint Read(string key, uint otherwise) =>
            TryGetResource(key, ActualThemeVariant, out var value) && value is global::Avalonia.Media.Color color
                ? (uint)color.R << 16 | (uint)color.G << 8 | color.B
                : otherwise;
    }
}
