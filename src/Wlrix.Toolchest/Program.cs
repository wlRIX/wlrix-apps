// SPDX-License-Identifier: GPL-3.0-or-later
// wlRIX Toolchest — the IRIX-style menu launcher anchored top-left of the desktop.
// Scaffold: prints a banner. Becomes an Avalonia app running as a Wayland client.

using Avalonia;
using ReactiveUI.Avalonia;

namespace Wlrix.Toolchest;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // AvaloniaConfiguration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().UseWayland()
        .With(new WaylandPlatformOptions { AppId = "com.wlrix.toolchest" }).LogToTrace().UseReactiveUI();
}
