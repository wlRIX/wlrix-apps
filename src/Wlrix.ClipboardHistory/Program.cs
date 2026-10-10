// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.ClipboardHistory.Services;

namespace Wlrix.ClipboardHistory;

sealed class Program
{
    /// <summary>
    /// The application's identity, and the identity of the one open popup.
    /// </summary>
    /// <remarks>
    /// The Wayland app id, which <c>wlrix-compositor</c> places by (under the pointer) and frames
    /// by (a titlebar and nothing else; see <c>placement.rs</c>), and the bus name the instance
    /// guard owns. One constant, because those must agree.
    /// </remarks>
    internal const string AppId = "com.wlrix.clipboard-history";

    /// <summary>The instance guard this process holds, or null when it never took one.</summary>
    internal static IInstanceGuard? Guard { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Before Avalonia, deliberately. A second launch closes the popup that is already open
        // and leaves without ever creating a window, so the key that opened it also dismisses it
        // with no flash of a second frame.
        var guard = new DBusInstanceGuard();
        if (!guard.TryAcquireAsync().GetAwaiter().GetResult())
        {
            guard.SendActivateAsync().GetAwaiter().GetResult();
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
