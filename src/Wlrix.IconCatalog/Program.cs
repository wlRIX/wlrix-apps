// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.Common.Instance;

namespace Wlrix.IconCatalog;

internal static class Program
{
    /// <summary>
    /// The Wayland app id, which the compositor frames and places the window by, and the bus
    /// name the instance guard owns. One constant, because those must agree; the desktop file's
    /// StartupWMClass says the same.
    /// </summary>
    internal const string AppId = "com.wlrix.iconcatalog";

    /// <summary>The instance guard this process holds, or null when it never took one.</summary>
    internal static DBusInstanceGuard? Guard { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Before Avalonia, deliberately. Find ▸ Icon Catalog a second time brings the window that
        // is already open forward, and this process leaves without ever making one of its own.
        var guard = new DBusInstanceGuard(AppId);
        if (!guard.TryAcquireAsync().GetAwaiter().GetResult())
        {
            guard.SendOpenAsync([]).GetAwaiter().GetResult();
            guard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        Guard = guard;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = AppId })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
