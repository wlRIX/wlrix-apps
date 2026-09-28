using System.Runtime.Versioning;
using Avalonia;
using ReactiveUI.Avalonia;

namespace Wlrix.Archiver;

// wlRIX is a Linux desktop and this program is only ever built for one, which is what the
// platform-compatibility analyzer wants said out loud for the POSIX-only code below it.
//
// The file dialogs are the portal's, which is why there is no UseManagedSystemDialogs call
// here any more. The Wayland backend prefers xdg-desktop-portal whenever it can export the
// parent window, and it can, because the compositor implements zxdg_exporter_v2 -- that used
// to mean a GTK dialog in the middle of an IRIX desktop, so the managed chooser was forced
// instead. xdg-desktop-portal-wlrix now answers FileChooser, so the preference lands on
// wlrix-file-picker: the same listing, icon theme and bookmarks as wlRIX Files, and the same
// dialog every sandboxed application here already gets.
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
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.archiver" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
