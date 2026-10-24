using Avalonia;
using ReactiveUI.Avalonia;
using Wlrix.Settings.International.Views;

namespace Wlrix.Settings.International;

sealed class Program
{
    /// <summary>The dialog <c>--page</c> asked for; Keyboard Settings' Layout button sends <c>keyboard</c>.</summary>
    internal static StartPage Page { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        Page = ParsePage(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary><c>--page country|language|keyboard|timezone</c>, or <c>--page=keyboard</c>.</summary>
    internal static StartPage ParsePage(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i] switch
            {
                "--page" when i + 1 < args.Count => args[i + 1],
                var arg when arg.StartsWith("--page=", StringComparison.Ordinal) => arg[7..],
                _ => null,
            };
            if (value is not null)
            {
                return value.ToLowerInvariant() switch
                {
                    "country" => StartPage.Country,
                    "language" => StartPage.Language,
                    "keyboard" => StartPage.Keyboard,
                    "timezone" or "time-zone" => StartPage.TimeZone,
                    _ => StartPage.None,
                };
            }
        }
        return StartPage.None;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseWayland()
            .With(new WaylandPlatformOptions { AppId = "com.wlrix.settings.international" })
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace()
            .UseReactiveUI();
}
