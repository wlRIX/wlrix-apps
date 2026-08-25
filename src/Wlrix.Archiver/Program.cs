using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Dialogs;
using ReactiveUI.Avalonia;

namespace Wlrix.Archiver;

// wlRIX is a Linux desktop and this program is only ever built for one. Saying so
// satisfies the platform-compatibility analyzer for `UseManagedSystemDialogs`, which is
// annotated as unavailable on the mobile and browser targets Avalonia also supports.
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
            // Forces Avalonia's in-process file picker, which `Wlrix.Avalonia` themes, instead of
            // the xdg-desktop-portal one.
            //
            // Left alone, the Wayland backend prefers the portal whenever it can export the
            // parent window — and it can, because the compositor implements `zxdg_exporter_v2`.
            // Where the GTK portal backend is installed that means a GTK dialog opens in the
            // middle of an IRIX desktop, and where it is not, `ManagedFileChooser` opens
            // untemplated and therefore blank. This one call settles both cases.
            .UseManagedSystemDialogs()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
