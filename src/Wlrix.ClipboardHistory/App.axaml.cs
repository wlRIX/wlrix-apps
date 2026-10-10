// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.ClipboardHistory.Localization;
using Wlrix.ClipboardHistory.Services;
using Wlrix.ClipboardHistory.ViewModels;
using Wlrix.ClipboardHistory.Views;
using Wlrix.Common.Localization;
using Wlrix.Theme;

namespace Wlrix.ClipboardHistory;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
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
            var model = new HistoryViewModel();
            var window = new HistoryWindow { DataContext = model };
            desktop.MainWindow = window;

            // Another press of the key. Arrives on a bus thread.
            if (Program.Guard is { } guard)
                guard.ActivateRequested += () => Dispatcher.UIThread.Post(window.Close);

            desktop.ShutdownRequested += (_, _) =>
            {
                model.Dispose();
                Program.Guard?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };

            // Shown first, and filled when the daemon answers: a popup summoned by a key should
            // appear at once, not after a round trip.
            _ = model.ConnectAsync(async () => await DBusClipboardHistory.ConnectAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
