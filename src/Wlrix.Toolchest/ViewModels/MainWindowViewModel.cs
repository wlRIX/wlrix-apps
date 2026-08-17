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
            // Disabled until they have somewhere to go: both need a themed confirmation of their
            // own, and neither should be a menu item that silently powers the machine off. The
            // shape is here so the work to come has somewhere to land -- the same reason
            // wlrix-desktop keeps its own unfinished items on screen and greyed out.
            new MenuNode(Strings.RestartSystem, isEnabled: false),
            new MenuNode(Strings.ShutDownSystem, isEnabled: false),
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
