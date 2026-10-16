using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Common.Localization;
using Wlrix.Pinentry.Localization;
using Wlrix.Pinentry.Services;
using Wlrix.Theme;

namespace Wlrix.Pinentry;

/// <summary>
/// One gpg-agent conversation, then exit. No window until gpg-agent asks a question, and one
/// window per question.
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            _ = ServeAsync(desktop);

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ServeAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // stdout is the protocol, so nothing else may write to it: Avalonia's trace logging goes
        // to stderr, and the server owns this writer.
        var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        var exitCode = 0;
        try
        {
            await Task.Run(() => new PinentryServer(input, output, new WindowUi()).RunAsync()).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // gpg-agent went away mid-conversation; there is nobody left to answer.
            exitCode = 1;
        }

        Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));
    }
}
