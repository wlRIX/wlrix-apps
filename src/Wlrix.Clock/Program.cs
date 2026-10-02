// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;

namespace Wlrix.Clock;

sealed class Program
{
    /// <summary>
    /// The Wayland app id. <c>wlrix-compositor</c> frames this window by it: a border and
    /// nothing else, since a clock has no use for a titlebar (see <c>placement::shell_frame</c>).
    /// </summary>
    internal const string AppId = "com.wlrix.clock";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = AppId })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
