// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using Avalonia;
using ReactiveUI.Avalonia;

namespace Wlrix.ImageView;

// wlRIX is a Linux desktop and this program is only ever built for one, which is what the
// platform-compatibility analyzer wants said out loud.
//
// The file dialogs are the portal's: xdg-desktop-portal-wlrix answers FileChooser with
// wlrix-file-picker, the same arrangement Wlrix.Archiver documents.
[SupportedOSPlatform("linux")]
internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.imageview" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
