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
    private readonly IApplicationCatalog _catalog;
    private readonly IAppLauncher _launcher;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(IApplicationCatalog catalog, IAppLauncher launcher,
        ILogger<MainWindowViewModel> logger)
    {
        _catalog = catalog;
        _launcher = launcher;
        _logger = logger;
        _launcher.LaunchFailed += OnLaunchFailed;
        BuildTopLevel(LoadingApplications());
    }

    /// <summary>Design-time constructor: a small static tree for the previewer.</summary>
    public MainWindowViewModel()
    {
        _catalog = null!;
        _launcher = null!;
        _logger = null!;
        BuildTopLevel(new MenuNode(Strings.Applications,
        [
            new MenuNode(Strings.Category("Network"), [new MenuNode("Firefox"), new MenuNode("Thunderbird")]),
            new MenuNode(Strings.Category("Utility"), [new MenuNode("Files"), new MenuNode("Text Editor")]),
        ]));
    }

    /// <summary>Raised (UI thread) to show the About dialog with the given message.</summary>
    public event Action<string>? ShowAbout;

    /// <summary>Raised (UI thread) to show a launch-error dialog with the given message.</summary>
    public event Action<string>? LaunchError;

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
            [new MenuNode(Strings.OpenUnixShell, command: new RelayCommand(() => _launcher.OpenTerminal()))]));
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

    private void RaiseAbout()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        ShowAbout?.Invoke(Strings.AboutMessage(version));
    }

    private void OnLaunchFailed(string message) => Dispatcher.UIThread.Post(() => LaunchError?.Invoke(message));
}
