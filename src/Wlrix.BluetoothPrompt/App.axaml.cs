using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wlrix.Common.Localization;
using Wlrix.BluetoothPrompt.Localization;
using Wlrix.BluetoothPrompt.Protocol;
using Wlrix.BluetoothPrompt.ViewModels;
using Wlrix.BluetoothPrompt.Views;
using Wlrix.Theme;

namespace Wlrix.BluetoothPrompt;

/// <summary>
/// One question, one window, then exit. The request is read before anything is shown, so a
/// malformed one costs no window at all.
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
            _ = AskAsync(desktop);

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task AskAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        PairingRequest request;
        try
        {
            var line = await Task.Run(() => Console.In.ReadLine()).ConfigureAwait(false);
            request = PairingRequest.Parse(line ?? string.Empty);
        }
        catch (FormatException err)
        {
            await Console.Error.WriteLineAsync($"wlrix-bluetooth-prompt: {err.Message}").ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => desktop.Shutdown(2));
            return;
        }

        var model = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var model = new PairingViewModel(request);
            new PairingWindow { DataContext = model }.Show();
            return model;
        });
        var answer = await model.Completion.ConfigureAwait(false);

        var exitCode = 1;
        if (answer is not null)
        {
            // stdout is the protocol: one line, nothing else written to it. A PIN leaves
            // through it and is not kept anywhere else.
            var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            await output.WriteLineAsync(answer.ToJson()).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            exitCode = 0;
        }

        Dispatcher.UIThread.Post(() => desktop.Shutdown(exitCode));
    }
}
