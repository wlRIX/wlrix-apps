using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Core.State;
using Wlrix.Files.ViewModels;
using Wlrix.Files.Views;
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.Files.Services;

/// <summary>
/// Every window the application has open, and the routing between them.
/// </summary>
/// <remarks>
/// This is the composition root's job, which is why it is the one place that knows both a view
/// model and a <see cref="Window"/>: something has to build the pair, and pushing that into
/// the view models would make them untestable for the sake of tidiness.
///
/// <para>
/// It implements <see cref="IWindowPresenter{T}"/> over the view model rather than over the
/// window, so the routing rules stay testable against a fake. Raising a window goes through an
/// event the window subscribes to, which keeps the reference pointing the way the rest of the
/// application already does.
/// </para>
/// </remarks>
public sealed class WindowManager(IServiceProvider services, FilesStateStore state, FilesSettings settings)
    : IWindowPresenter<MainWindowViewModel>, IWindowRouting<MainWindowViewModel>
{
    /// <summary>How long a change waits before the session file is rewritten.</summary>
    /// <remarks>
    /// Long enough that dragging a window's edge writes once rather than sixty times a
    /// second, short enough that a crash loses at most the last couple of seconds of
    /// rearranging. The write also happens unconditionally on shutdown.
    /// </remarks>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly List<(MainWindowViewModel Model, Window Window)> _open = [];
    private DispatcherTimer? _save;

    /// <summary>Raised when the last window closes, so the application can shut down.</summary>
    public event Action? AllWindowsClosed;

    /// <summary>How many windows are open.</summary>
    public int Count => Router.Registry.Count;

    /// <summary>The routing policy. Public so the view models can ask it to open things.</summary>
    public WindowRouter<MainWindowViewModel> Router { get; private set; } = null!;

    /// <summary>Wires the router to this manager. Called once, by the application.</summary>
    /// <remarks>
    /// Separate from the constructor because the router needs the presenter and the presenter
    /// is this object: one of the two references has to be filled in afterwards, and doing it
    /// here keeps the awkwardness in one place rather than in the routing rules.
    /// </remarks>
    public void Start()
    {
        Router = new WindowRouter<MainWindowViewModel>(this, () => settings.Current.NavigationMode);
        _save = new DispatcherTimer { Interval = SaveDelay };
        _save.Tick += (_, _) =>
        {
            _save!.Stop();
            SaveSession();
        };
    }

    /// <summary>
    /// Tells every window that <c>files.toml</c> changed underneath it.
    /// </summary>
    /// <remarks>
    /// What a <c>SIGHUP</c> reaches. A hand-edited file and a settings panel arrive the same
    /// way, which is the point of binding the menus to the file rather than to the click: the
    /// window that made the change and the three that did not all end up agreeing.
    /// </remarks>
    public void ConfigurationChanged()
    {
        foreach (var window in Router.Registry.Windows)
        {
            window.RaiseNavigationMode();
            window.ReloadIcons();
        }
    }

    /// <inheritdoc/>
    public MainWindowViewModel Create(Location location) => Create(location, null);

    /// <summary>Builds a window, optionally restoring how it was left.</summary>
    /// <remarks>
    /// The saved state is applied to the view model <i>before</i> the window is shown, so a
    /// restored rail is the right width when it first appears rather than snapping to it a
    /// frame later.
    /// </remarks>
    private MainWindowViewModel Create(Location location, WindowSession? saved)
    {
        var model = services.GetRequiredService<MainWindowViewModel>();
        model.StartAt(location);
        if (saved is { SidebarWidth: > 0 })
            model.SidebarWidth = saved.SidebarWidth;

        // The preferences are one record shared by every window, so a checkbox ticked in one
        // has to redraw the same checkbox in the others.
        model.PreferencesChanged += RelayPreferences;
        model.BookmarksChanged += RelayBookmarks;
        model.SharesChanged += RelayShares;
        model.SessionChanged += ScheduleSave;

        var window = new MainWindow
        {
            DataContext = model,
            // So the connect dialog can say plainly whether "remember" survives a restart.
            CanRememberPasswords = services.GetRequiredService<ICredentialStore>().IsPersistent
        };
        if (saved is { Width: > 0, Height: > 0 })
        {
            window.Width = saved.Width;
            window.Height = saved.Height;
        }
        _open.Add((model, window));
        // The size is the view's to know, so the view is what reports it changing.
        window.SizeChanged += (_, _) => ScheduleSave();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
                ScheduleSave();
        };
        model.PresentRequested += () =>
        {
            window.Show();
            // Activate is what raises it. Under Wayland that is xdg_activation_v1, which the
            // vic485/Avalonia fork had to grow for this: the stock backend's Activate is an
            // empty method, and Classic mode is unimplementable without it.
            window.Activate();
        };

        window.Closed += (_, _) =>
        {
            model.PreferencesChanged -= RelayPreferences;
            model.BookmarksChanged -= RelayBookmarks;
            model.SharesChanged -= RelayShares;
            model.SessionChanged -= ScheduleSave;
            _open.RemoveAll(entry => ReferenceEquals(entry.Model, model));
            Router.Forget(model);
            // Written before the model is disposed and now rather than on the debounce: the
            // last window closing is also the application closing, and the timer would not
            // get another chance to fire.
            SaveSession();
            model.Dispose();
            if (Count == 0)
                AllWindowsClosed?.Invoke();
        };

        window.Show();
        return model;
    }

    /// <summary>
    /// Opens everything a second invocation handed over, and comes forward.
    /// </summary>
    /// <remarks>
    /// An empty list is <c>Activate</c> rather than <c>Open</c>: somebody launched the file
    /// manager with no arguments while one was already running, and the useful answer to that
    /// is the window they already have, raised.
    /// </remarks>
    public void OpenAll(IReadOnlyList<string> uris)
    {
        var front = Router.Registry.Windows.LastOrDefault();
        foreach (var uri in uris)
        {
            if (!Location.TryParse(uri, out var location))
                continue;
            if (front is null)
            {
                front = Create(location);
                Router.Track(front, location);
                continue;
            }
            Router.Open(location, Router.Mode == NavigationMode.Classic
                ? OpenIntent.NewWindow
                : OpenIntent.NewTab, front);
        }

        if (uris.Count == 0 && front is not null)
            Present(front);
    }

    // --- the session ------------------------------------------------------

    private void ScheduleSave()
    {
        if (_save is not { } timer)
            return;
        // Restarted rather than left running, so a burst of changes writes once at the end
        // rather than once in the middle of it.
        timer.Stop();
        timer.Start();
    }

    /// <summary>Writes the open windows and tabs. Also called on shutdown.</summary>
    public void SaveSession() => state.SaveSession(
    [
        .. _open.Select(entry => new WindowSession
        {
            Width = entry.Window.Width,
            Height = entry.Window.Height,
            Maximized = entry.Window.WindowState == WindowState.Maximized,
            SidebarWidth = entry.Model.SidebarWidth,
            ActiveTab = entry.Model.ActiveTabIndex,
            Tabs = entry.Model.TabSessions
        })
    ]);

    /// <summary>
    /// Reopens what the last run left open. Answers false if there was nothing to reopen.
    /// </summary>
    /// <remarks>
    /// A window with no tabs left in it — every one of them somewhere that no longer exists —
    /// is skipped rather than opened empty. The locations themselves are not checked: a share
    /// that is slow to answer should still come back, and the listing already knows how to
    /// show a directory it cannot read.
    /// </remarks>
    public bool Restore()
    {
        var restored = false;
        foreach (var saved in state.Session)
        {
            var tabs = saved.Tabs
                .Select(tab => Location.TryParse(tab.Uri, out var location) ? (Saved: tab, Location: location) : default)
                .Where(pair => pair.Location is not null)
                .ToList();
            if (tabs.Count == 0)
                continue;

            var model = Create(tabs[0].Location!, saved);
            Router.Track(model, tabs[0].Location!);
            foreach (var (_, location) in tabs.Skip(1))
                model.AddTab(location!);

            // Splits after every tab exists, so restoring one cannot disturb the order of the
            // others or which of them ends up selected.
            for (var i = 0; i < tabs.Count && i < model.Tabs.Count; i++)
            {
                if (tabs[i].Saved.SecondUri is { } uri && Location.TryParse(uri, out var second))
                    model.RestoreSplit(model.Tabs[i], second, tabs[i].Saved.ActivePane);
            }

            if (saved.ActiveTab >= 0 && saved.ActiveTab < model.Tabs.Count)
                model.ActiveTab = model.Tabs[saved.ActiveTab];

            if (saved.Maximized)
                Maximize(model);
            restored = true;
        }
        return restored;
    }

    /// <remarks>
    /// Size but not position throughout: Wayland gives a client no position at all, so the
    /// compositor places the window and a saved position would be a number written and never
    /// read.
    /// </remarks>
    private void Maximize(MainWindowViewModel model)
    {
        if (_open.FirstOrDefault(entry => ReferenceEquals(entry.Model, model)).Window is { } window)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>Opens a fresh window at the user's home directory.</summary>
    /// <remarks>
    /// What the desktop entry's New Window action asks for. Always a window, in both modes:
    /// the action says so in its name, and there is no directory involved for the routing
    /// policy to have an opinion about.
    /// </remarks>
    public MainWindowViewModel OpenNewWindow()
    {
        var home = services.GetRequiredService<XdgUserDirs>().Home;
        var model = Create(home);
        Router.Track(model, home);
        return model;
    }

    private void RelayPreferences()
    {
        foreach (var window in Router.Registry.Windows.ToList())
            window.RefreshPreferences();
    }

    /// <summary>Bookmarks are one file, so every window's rail follows a change in any of them.</summary>
    private void RelayBookmarks()
    {
        foreach (var window in Router.Registry.Windows.ToList())
            window.RefreshBookmarks();
    }

    /// <summary>And so is the saved share list, which every Internet menu shows.</summary>
    private void RelayShares()
    {
        foreach (var window in Router.Registry.Windows.ToList())
            window.RefreshShares();
    }

    /// <inheritdoc/>
    public void Present(MainWindowViewModel window) => window.RequestPresent();

    /// <inheritdoc/>
    public void Open(Location location, OpenIntent intent, MainWindowViewModel from) =>
        Router.Open(location, intent, from);

    /// <inheritdoc/>
    public MainWindowViewModel? Claim(MainWindowViewModel window, Location location) =>
        Router.TryClaim(window, location);

    /// <inheritdoc/>
    public void Resync(MainWindowViewModel window, Location location)
    {
        if (!Router.Registry.Rekey(window, location))
            Router.Registry.Unkey(window);
    }

    /// <inheritdoc/>
    public void NavigateInPlace(MainWindowViewModel window, Location location) =>
        _ = window.Pane.NavigateAsync(location);

    /// <inheritdoc/>
    public void AddTab(MainWindowViewModel window, Location location)
    {
        window.AddTab(location);
        window.RequestPresent();
    }
}
