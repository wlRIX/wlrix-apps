using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.SourcePicker.Localization;
using Wlrix.SourcePicker.Models;
using Wlrix.SourcePicker.ViewModels;
using Wlrix.SourcePicker.Views;
using Wlrix.Theme;

namespace Wlrix.SourcePicker;

public partial class App : Application
{
    private readonly Manifest _manifest;

    /// <summary>Only for the XAML designer, which cannot pass a manifest.</summary>
    public App() : this(new Manifest())
    {
    }

    public App(Manifest manifest) => _manifest = manifest;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. Not awaited:
        // it reaches the settings daemon over the bus, and a window's first paint does not
        // wait on a color. Nothing here fails if there is no settings service.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new PickerViewModel(_manifest);
            var window = new PickerWindow { DataContext = model };

            // The window closing is the end of the question, however it closed. Taking the
            // answer here rather than from the Share button means every other way out --
            // Escape, the frame's close button, the compositor withdrawing the surface --
            // lands on the same path and reports a cancel.
            window.Closed += (_, _) =>
            {
                model.Dispose();
                desktop.Shutdown(Program.Answer(window.Result));
            };

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
