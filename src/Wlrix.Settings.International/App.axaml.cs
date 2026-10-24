using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Settings.International.Localization;
using Wlrix.Settings.International.ViewModels;
using Wlrix.Settings.International.Views;
using Wlrix.Theme;

namespace Wlrix.Settings.International;

public partial class App : Application
{
    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new InternationalViewModel();
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;

            // The window is shown first and filled in when the lists and the daemons answer, as in
            // the other settings panels; then the dialog --page named, if any.
            _ = StartAsync(model, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(InternationalViewModel model, MainWindow window)
    {
        await model.InitializeAsync();
        if (Program.Page == StartPage.None)
            return;

        // A dialog needs its owner on screen. The lists usually take longer than the first paint,
        // but not on every machine.
        if (!window.IsVisible)
        {
            var opened = new TaskCompletionSource();
            window.Opened += (_, _) => opened.TrySetResult();
            await opened.Task;
        }
        await window.OpenAsync(Program.Page);
    }
}
