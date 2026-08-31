using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Wlrix.Toolchest.Applications;
using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.Services;
using ZLogger;

namespace Wlrix.Toolchest.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>The installed name of the Desks Overview, launched by Desktop → Extra Desks.</summary>
    private const string DesksProgram = "wlrix-desks";

    /// <summary>The installed name of the Software Manager, launched by System → Software Manager.</summary>
    private const string SoftwareManagerProgram = "wlrix-software-manager";

    /// <summary>
    /// The installed name of the Shut Down System dialog, behind both of the System menu's
    /// machine-wide items.
    /// </summary>
    private const string ShutdownProgram = "wlrix-shutdown";

    /// <summary>
    /// What tells the dialog it was reached through Restart System rather than Shut Down
    /// System. It only presets the checkbox; the choice is still the user's until they press
    /// OK, which is the whole reason these two items open a window instead of acting.
    /// </summary>
    private const string RestartFlag = "--restart";

    private readonly IApplicationCatalog _catalog;
    private readonly IAppLauncher _launcher;
    private readonly ISessionService _session;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(IApplicationCatalog catalog, IAppLauncher launcher,
        ISessionService session, ILogger<MainWindowViewModel> logger)
    {
        _catalog = catalog;
        _launcher = launcher;
        _session = session;
        _logger = logger;
        _launcher.LaunchFailed += OnLaunchFailed;
        BuildTopLevel(LoadingApplications());
    }

    /// <summary>Design-time constructor: a small static tree for the previewer.</summary>
    public MainWindowViewModel()
    {
        _catalog = null!;
        _launcher = null!;
        _session = null!;
        _logger = null!;
        BuildTopLevel(new MenuNode(Strings.Applications,
        [
            new MenuNode(Strings.Category("Network"), [new MenuNode("Firefox"), new MenuNode("Thunderbird")]),
            new MenuNode(Strings.Category("Utility"), [new MenuNode("Files"), new MenuNode("Text Editor")]),
        ]));
    }

    /// <summary>Raised (UI thread) to show the About dialog with the given message.</summary>
    public event Action<string>? ShowAbout;

    /// <summary>Raised (UI thread) to show an error dialog with the given message.</summary>
    public event Action<string>? ShowError;

    /// <summary>
    /// Raised (UI thread) to ask the user to confirm ending the session; the handler answers
    /// whether they did. Nothing is signaled until it says yes — with no handler attached,
    /// Log Out does nothing rather than logging out unasked.
    /// </summary>
    public event Func<Task<bool>>? ConfirmLogOut;

    public ObservableCollection<MenuNode> TopLevel { get; } = [];

    public string Title => Strings.Toolchest;

    /// <summary>Loads (and caches) the application catalog, then fills the Applications submenu.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var catalog = await _catalog.LoadAsync();
            Dispatcher.UIThread.Post(() => BuildTopLevel(BuildApplications(catalog)));
        }
        catch (Exception ex)
        {
            _logger.ZLogError(ex, $"Failed to load the application catalog.");
        }
    }

    private void BuildTopLevel(MenuNode applications)
    {
        TopLevel.Clear();
        TopLevel.Add(new MenuNode(Strings.Desktop,
        [
            new MenuNode(Strings.ExtraDesks,
                command: new RelayCommand(() => _launcher.Run(Strings.ExtraDesks, DesksProgram))),
            MenuNode.Separator(),
            new MenuNode(Strings.OpenTerminal, command: new RelayCommand(() => _launcher.OpenTerminal())),
            MenuNode.Separator(),
            new MenuNode(Strings.LogOut, command: new RelayCommand(() => _ = LogOutAsync())),
        ]));
        TopLevel.Add(new MenuNode(Strings.System,
        [
            new MenuNode(Strings.SoftwareManager,
                command: new RelayCommand(() => _launcher.Run(Strings.SoftwareManager, SoftwareManagerProgram))),
            MenuNode.Separator(),
            // Neither acts: both open the Shut Down System dialog, which is where the machine
            // actually gets asked to go down. That indirection is the point -- a menu item that
            // powered the box off on a mis-click would be unrecoverable in a way no other item
            // here is -- and it is why these two stopped being grayed out.
            new MenuNode(Strings.RestartSystem,
                command: new RelayCommand(() =>
                    _launcher.Run(Strings.RestartSystem, ShutdownProgram, RestartFlag))),
            new MenuNode(Strings.ShutDownSystem,
                command: new RelayCommand(() => _launcher.Run(Strings.ShutDownSystem, ShutdownProgram))),
        ]));
        TopLevel.Add(applications);
        TopLevel.Add(new MenuNode(Strings.Help,
            [new MenuNode(Strings.AboutToolchest, command: new RelayCommand(RaiseAbout))]));
    }

    private static MenuNode LoadingApplications() =>
        new(Strings.Applications, [new MenuNode(Strings.Loading, isEnabled: false)]);

    private MenuNode BuildApplications(Catalog catalog)
    {
        if (catalog.Categories.Count == 0)
            return LoadingApplications();

        var categories = catalog.Categories
            .Select(category => new MenuNode(
                Strings.Category(category.Id),
                category.Apps
                    .Select(app => new MenuNode(app.Name,
                        command: new RelayCommand(() => _launcher.Launch(app.Name, app.Exec, app.Terminal))))
                    .ToList()))
            .ToList();

        return new MenuNode(Strings.Applications, categories);
    }

    /// <summary>
    /// Ends the session, once the user has confirmed it. Losing every open application to a
    /// mis-click on a menu is not recoverable, so this one asks first — which the Toolchest can
    /// do and the desktop's own Log Out cannot, having nowhere to put a dialog.
    /// </summary>
    private async Task LogOutAsync()
    {
        if (ConfirmLogOut is not { } confirm || !await confirm())
            return;

        if (_session.LogOut() is { } why)
            ShowError?.Invoke(why);
    }

    private void RaiseAbout()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        ShowAbout?.Invoke(Strings.AboutMessage(version));
    }

    private void OnLaunchFailed(string message) => Dispatcher.UIThread.Post(() => ShowError?.Invoke(message));
}
