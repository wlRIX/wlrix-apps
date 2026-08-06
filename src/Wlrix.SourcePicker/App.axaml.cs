using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.SourcePicker.Models;
using Wlrix.SourcePicker.ViewModels;
using Wlrix.SourcePicker.Views;

namespace Wlrix.SourcePicker;

public partial class App : Application
{
    private readonly Manifest _manifest;

    /// <summary>Only for the XAML designer, which cannot pass a manifest.</summary>
    public App() : this(new Manifest())
    {
    }

    public App(Manifest manifest) => _manifest = manifest;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
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
