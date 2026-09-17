using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Wlrix.Common.Localization;
using Wlrix.Files.Core.Config;
using Wlrix.Files.Core.Filesystems;
using Wlrix.Files.Core.Icons;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Portal;
using Wlrix.Files.Core.State;
using Wlrix.FilePicker.Localization;
using Wlrix.FilePicker.Services;
using Wlrix.FilePicker.ViewModels;
using Wlrix.FilePicker.Views;
using Wlrix.Theme;

namespace Wlrix.FilePicker;

public partial class App : Application
{
    private readonly FileChooserRequest _request;

    /// <summary>Only for the XAML designer, which cannot pass a request.</summary>
    public App() : this(new FileChooserRequest())
    {
    }

    public App(FileChooserRequest request) => _request = request;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this, and a
        // catalog set afterwards would leave every static label showing its own key.
        TrExtension.Catalog = Strings.Catalog;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Draw in the session's color scheme, and follow it when it changes. Not awaited: it
        // reaches the settings daemon over the bus, and a dialog's first paint does not wait
        // on a color. Nothing here fails if there is no settings service.
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = Build();

            // The window closing is the end of the question, however it closed. Taking the
            // answer here rather than from the accept button means every other way out --
            // Escape, the frame's close button, the compositor withdrawing the surface --
            // lands on the same path and reports a cancel. Recorded rather than acted on:
            // `Program.Main` writes it and chooses the exit code once the toolkit has gone.
            window.Closed += (_, _) => Program.Result = window.Result;

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private PickerWindow Build()
    {
        // files.toml, for the icon theme. Read rather than asked for over the settings daemon,
        // which is the convention of the whole stack -- and this process lives for one dialog,
        // so there is nothing for a live reload to reach.
        var config = FilesConfig.Load();
        var theme = new XdgIconTheme { Theme = config.IconTheme };
        var icons = new IconService(SharedMimeDatabase.Load(), new IconLoader(theme));

        var dirs = XdgUserDirs.Read();
        // The file manager's own state, read and never written. Its bookmarks belong in this
        // rail; its window sessions and view state are no business of a dialog, and writing
        // any of it from here would have two programs owning one file.
        var state = new FilesStateStore();

        var model = new PickerViewModel(
            _request,
            new LocalFileSystem(),
            icons,
            PickerViewModel.ReadPlaces(dirs, state),
            _request.StartingFolder(dirs.Home, System.IO.Directory.Exists));

        return new PickerWindow { DataContext = model };
    }
}
