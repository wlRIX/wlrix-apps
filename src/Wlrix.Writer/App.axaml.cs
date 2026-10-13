// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.Theme;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Services;
using Wlrix.Writer.ViewModels;
using Wlrix.Writer.Views;

namespace Wlrix.Writer;

public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>The single-instance guard <c>Program</c> acquired, or null under the designer.</summary>
    internal static DBusInstanceGuard? Guard { get; set; }

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

        // Reading the grammar package takes a moment; done here, on a worker, it is ready by the
        // time the first file has been read.
        _ = Task.Run(() => WriterTheme.Shared);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("writer");
            services.AddTransient<MainWindowViewModel>();
            _services = services.BuildServiceProvider();

            var model = _services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = model };

            // Files named on the command line open once the window is up, so a failure to read
            // one has a window to report into.
            window.OpenOnStartup(desktop.Args ?? []);

            // Another launch's files arrive here, on a bus thread.
            if (Guard is { } guard)
                guard.OpenRequested += uris => Dispatcher.UIThread.Post(() => _ = model.OpenUrisAsync(uris));

            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) =>
            {
                // Dispose the provider on shutdown so ZLogger flushes its buffered output, and
                // give the bus name up so the next launch is a first one.
                _services.Dispose();
                Guard?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
