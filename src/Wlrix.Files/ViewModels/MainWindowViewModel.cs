using System.Globalization;
using System.Reactive;
using Avalonia.Collections;
using Microsoft.Extensions.Logging;
using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Dnd;
using Wlrix.Files.Core.Filesystems;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Operations;
using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Platform;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Core.Search;
using Wlrix.Files.Core.State;
using Wlrix.Files.Core.Thumbnails;
using Wlrix.Files.Localization;
using Wlrix.Files.Services;
using Wlrix.Files.Views;
using ZLogger;

namespace Wlrix.Files.ViewModels;

/// <summary>The window: chrome, the places rail, and the one pane it currently holds.</summary>
/// <remarks>
/// Tabs and split panes are not here yet, but the pane is already a separate object so that
/// adding them is a collection rather than a rewrite.
/// </remarks>
public sealed class MainWindowViewModel : ReactiveObject, IDisposable
{
    private readonly FileSystemProvider _provider;
    private readonly DragStaging _staging;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Func<Location, PaneViewModel> _newPane;
    private readonly IWindowRouting<MainWindowViewModel> _routing;
    private readonly FilesStateStore _state;
    private readonly ApplicationHandlers _handlers;
    private readonly ApplicationLauncher _launcher;
    private readonly ICredentialStore _credentials;
    private readonly SharedMimeDatabase _mime;
    private readonly IStorageDeviceMonitor _devices;
    private readonly FilesSettings _settings;
    private TabViewModel _activeTab;
    private IReadOnlyList<FileEntryViewModel> _selection = [];
    private double _sidebarWidth = 170;
    private QueuedOperation? _running;
    private string? _operationStatus;
    private double? _operationFraction;

    public MainWindowViewModel(
        FileSystemProvider provider,
        ILoggerFactory loggerFactory,
        XdgUserDirs userDirs,
        MountTable mounts,
        IconService icons,
        ThumbnailService thumbnails,
        ApplicationHandlers handlers,
        ApplicationLauncher launcher,
        OperationQueue operations,
        FileClipboard clipboard,
        DragStaging staging,
        IWindowRouting<MainWindowViewModel> routing,
        FilesStateStore state,
        ICredentialStore credentials,
        SharedMimeDatabase mime,
        IStorageDeviceMonitor devices,
        FilesSettings settings)
    {
        _credentials = credentials;
        _mime = mime;
        _devices = devices;
        _settings = settings;
        devices.Changed += () => Dispatcher.UIThread.Post(RefreshDevices);
        RefreshDevices();
        _routing = routing;
        _state = state;
        _provider = provider;
        _staging = staging;
        _logger = loggerFactory.CreateLogger<MainWindowViewModel>();
        Icons = icons;
        Thumbnails = thumbnails;
        _handlers = handlers;
        _launcher = launcher;
        launcher.Failed += message => ErrorRaised?.Invoke(message);
        thumbnails.Enabled = state.Preferences.ThumbnailsEnabled;
        Operations = operations;
        Clipboard = clipboard;

        _newPane = start =>
        {
            var pane = new PaneViewModel(provider, loggerFactory.CreateLogger<PaneViewModel>(), start);
            pane.ErrorRaised += message => ErrorRaised?.Invoke(message);
            // A queue full of previews for the directory just left is a queue of work that
            // delays the previews for the one just arrived at.
            pane.Invalidated += Thumbnails.Invalidate;
            return pane;
        };

        _activeTab = new TabViewModel(this, _newPane(userDirs.Home));
        _activeTab.Moved += RaiseSessionChanged;
        _activeTab.ActivePaneChanged += OnActivePaneChanged;
        Tabs.Add(_activeTab);

        Places = BuildPlaces(userDirs);
        RefreshBookmarks();
        RefreshShares();
        Mounts = mounts;
        Home = userDirs.Home;

        GoBack = Command(() => Stepped(Pane.GoBackAsync()));
        GoForward = Command(() => Stepped(Pane.GoForwardAsync()));
        GoUp = Command(() => Stepped(Pane.GoUpAsync()));
        GoHome = Command(() => NavigateActiveTabAsync(userDirs.Home));
        Refresh = Command(() => Pane.ReloadAsync());
        About = ReactiveCommand.Create(() => AboutRequested?.Invoke());
        Close = ReactiveCommand.Create(() => CloseRequested?.Invoke());

        CopySelection = Command(() => { clipboard.Copy(SelectedLocations()); return Task.CompletedTask; });
        CutSelection = Command(() => { clipboard.Cut(SelectedLocations()); return Task.CompletedTask; });
        Paste = Command(PasteAsync);
        RenameSelection = Command(RenameAsync);
        MakeCopy = Command(MakeCopyAsync);
        MakeReference = Command(MakeReferenceAsync);
        NewFolder = Command(NewFolderAsync);
        TrashSelection = Command(TrashAsync);
        DeleteSelection = Command(DeleteAsync);
        NewTab = ReactiveCommand.Create(() => { AddTab(Pane.Location); });
        OpenSelection = Command(() => OpenSelectionAsync(OpenIntent.Default));
        OpenSelectionInNewTab = Command(() => OpenSelectionAsync(OpenIntent.NewTab));
        SelectAll = ReactiveCommand.Create(() => SelectAllRequested?.Invoke());
        ToggleBookmark = ReactiveCommand.Create(ToggleBookmarkHere);
        ToggleDefaultFileManager = ReactiveCommand.Create(SetOrClearDefaultFileManager);
        ConnectToServer = Command(ConnectAsync);
        Disconnect = Command(DisconnectAsync);
        CloseTab = ReactiveCommand.Create(() => CloseActiveTab());
        NextTab = ReactiveCommand.Create(() => CycleTab(1));
        PreviousTab = ReactiveCommand.Create(() => CycleTab(-1));
        Find = Command(FindAsync);
        ToggleSplit = ReactiveCommand.Create(SplitOrUnsplit);
        ShowProperties = Command(ShowPropertiesAsync);
        ShowFolderProperties = Command(ShowFolderPropertiesAsync);

        operations.ActiveChanged += () => Dispatcher.UIThread.Post(RefreshOperation);
    }

    /// <summary>The window's tabs, in strip order. Never empty: closing the last one closes
    /// the window.</summary>
    public AvaloniaList<TabViewModel> Tabs { get; } = [];

    /// <summary>Which tab is showing.</summary>
    /// <remarks>
    /// Setting it re-raises <see cref="Pane"/>, which is what makes every
    /// <c>{Binding Pane.…}</c> in the chrome — the path bar, the status line, the sort and
    /// view menus — follow the switch without any of them knowing tabs exist.
    /// </remarks>
    public TabViewModel ActiveTab
    {
        get => _activeTab;
        set
        {
            if (ReferenceEquals(_activeTab, value) || !Tabs.Contains(value))
                return;
            _activeTab = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Pane));
            // The window is keyed by whatever its active tab is showing, so switching tabs
            // moves the key -- otherwise the registry would answer with a window whose front
            // tab is somewhere else entirely.
            _routing.Resync(this, value.Pane.Location);
            // The listing is rebuilt by the TabControl, so it asks the tab what was selected
            // rather than the other way round.
            Selection = value.Selection;
            SessionChanged?.Invoke();
        }
    }

    /// <summary>The active tab's pane. Everything that is not tab-aware goes through here.</summary>
    public PaneViewModel Pane => _activeTab.Pane;

    /// <summary>Whether the tab strip is worth showing.</summary>
    /// <remarks>
    /// Hidden at one tab, because IRIX's fm had none and a strip with a single tab in it is
    /// a row of chrome that says nothing.
    /// </remarks>
    public bool HasMultipleTabs => Tabs.Count > 1;

    /// <summary>Where Home goes, and what a new window opens at.</summary>
    public Location Home { get; }

    // --- preferences ------------------------------------------------------
    //
    // Surfaced one property at a time rather than by exposing the record, because a menu item
    // binds to a bool and because each setter has to write the file. They are the application's
    // settings, not the window's, so every open window sees a change immediately.

    /// <summary>The desktop entry that names this application.</summary>
    public const string DesktopEntry = "com.wlrix.files.desktop";

    /// <summary>Whether opening a folder anywhere on the system lands here.</summary>
    /// <remarks>
    /// Read from the file each time rather than cached: the user can change it from another
    /// application, or by editing the file, and a stale checkmark would be worse than none.
    /// </remarks>
    public bool IsDefaultFileManager
    {
        get
        {
            try
            {
                return MimeAppsList.Read(MimeAppsList.UserPath).DefaultFor(DirectoryMimeType) == DesktopEntry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Makes this the default file manager, or gives the role back.
    /// </summary>
    /// <remarks>
    /// A user-scope write to <c>~/.config/mimeapps.list</c>, which is the only honest place
    /// for it: installing the desktop entry makes the application <i>eligible</i> to open a
    /// folder and nothing more, and an installer that silently claimed the role system-wide
    /// would be taking a decision that is not its to take.
    /// </remarks>
    public void SetOrClearDefaultFileManager()
    {
        try
        {
            var path = MimeAppsList.UserPath;
            var list = MimeAppsList.Read(path);
            if (list.DefaultFor(DirectoryMimeType) == DesktopEntry)
                list.ClearDefault(DirectoryMimeType, DesktopEntry);
            else
                list.SetDefault(DirectoryMimeType, DesktopEntry);
            list.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.ZLogWarning(ex, $"could not update the default file manager");
            ErrorRaised?.Invoke(ex.Message);
        }
        this.RaisePropertyChanged(nameof(IsDefaultFileManager));
    }

    private const string DirectoryMimeType = "inode/directory";

    public bool IsClassicMode => _settings.Current.NavigationMode == NavigationMode.Classic;

    public bool IsModernMode => !IsClassicMode;

    /// <summary>
    /// Switches between IRIX and Dolphin behavior, and tells every window.
    /// </summary>
    /// <remarks>
    /// Written through <c>wlrix-settings-daemon</c> rather than into this application's own
    /// state, because it is a setting somebody would look for in a settings panel and the
    /// daemon is the only thing allowed to write a wlRIX config file. The menu follows the
    /// file rather than the click: if the write is refused, the tick stays where it was.
    /// </remarks>
    public async Task SetNavigationModeAsync(NavigationMode mode)
    {
        if (_settings.Current.NavigationMode == mode)
            return;

        if (await _settings.SetNavigationModeAsync(mode).ConfigureAwait(true) is { } problem)
        {
            ErrorRaised?.Invoke(Strings.SettingFailed(problem));
            return;
        }

        RaiseNavigationMode();
    }

    /// <summary>Tells the window the mode moved, whoever moved it.</summary>
    /// <remarks>
    /// Also what a SIGHUP reaches: a hand-edited files.toml and a settings panel both arrive
    /// here, which is why the menu is bound to the file rather than to what was last clicked.
    /// </remarks>
    public void RaiseNavigationMode()
    {
        this.RaisePropertyChanged(nameof(IsClassicMode));
        this.RaisePropertyChanged(nameof(IsModernMode));
        PreferencesChanged?.Invoke();
    }

    /// <summary>Makes every row ask for its icon again, after a theme change.</summary>
    /// <remarks>
    /// Both halves are needed. Clearing is what lets a row ask at all, because a row that has
    /// an icon never asks twice; the event is what makes the rows already on screen do it now
    /// rather than when something happens to realize them again.
    /// </remarks>
    public void ReloadIcons()
    {
        foreach (var tab in Tabs)
        {
            foreach (var pane in tab.Panes)
            {
                foreach (var row in pane.Entries)
                    row.ClearIcon();
            }
        }

        VisualsInvalidated?.Invoke();
    }

    public bool SingleClickOpen
    {
        get => _state.Preferences.SingleClickOpen;
        set
        {
            if (_state.Preferences.SingleClickOpen == value)
                return;
            _state.Preferences = _state.Preferences with { SingleClickOpen = value };
            PreferencesChanged?.Invoke();
        }
    }

    public bool ConfirmDelete
    {
        get => _state.Preferences.ConfirmDelete;
        set
        {
            if (_state.Preferences.ConfirmDelete == value)
                return;
            _state.Preferences = _state.Preferences with { ConfirmDelete = value };
            PreferencesChanged?.Invoke();
        }
    }

    /// <summary>Raised on this window when a preference changes anywhere.</summary>
    /// <remarks>
    /// The preferences are one record shared by every window, so a checkbox ticked in one has
    /// to redraw the same checkbox in the others. The application relays this to them all.
    /// </remarks>
    public event Action? PreferencesChanged;

    /// <summary>Re-reads every preference-backed property. Called when another window wrote one.</summary>
    public void RefreshPreferences()
    {
        this.RaisePropertyChanged(nameof(IsClassicMode));
        this.RaisePropertyChanged(nameof(IsModernMode));
        this.RaisePropertyChanged(nameof(SingleClickOpen));
        this.RaisePropertyChanged(nameof(ConfirmDelete));
        this.RaisePropertyChanged(nameof(ThumbnailsEnabled));
        Thumbnails.Enabled = _state.Preferences.ThumbnailsEnabled;
    }

    /// <summary>Points a freshly built window at a directory, before it is shown.</summary>
    public void StartAt(Location location) => _activeTab.Pane.ResetTo(location);

    /// <summary>Asks the view to bring this window to the front.</summary>
    public void RequestPresent() => PresentRequested?.Invoke();

    /// <summary>
    /// Raised when anything the session file records has changed.
    /// </summary>
    /// <remarks>
    /// Which tabs there are, where they point, and which is in front. The window's own size
    /// is not in here — the view watches that, because a view model has no idea how big its
    /// window is.
    /// </remarks>
    public event Action? SessionChanged;

    /// <summary>The tabs, as the session file records them.</summary>
    public IReadOnlyList<TabSession> TabSessions =>
        [.. Tabs.Select(tab => new TabSession(
            tab.Panes[0].Location.ToUriString(),
            tab.Panes.Count > 1 ? tab.Panes[1].Location.ToUriString() : null,
            tab.Panes.IndexOf(tab.ActivePane)))];

    /// <summary>Which tab is in front, by index.</summary>
    public int ActiveTabIndex => Math.Max(0, Tabs.IndexOf(_activeTab));

    /// <summary>Raised when the window should be shown and raised.</summary>
    /// <remarks>
    /// An event rather than a window reference, so the view model still knows nothing about
    /// Avalonia and the routing tests can watch for it with no display.
    /// </remarks>
    public event Action? PresentRequested;

    public IReadOnlyList<PlaceViewModel> Places { get; }

    /// <summary>
    /// The saved locations, below Places in the rail.
    /// </summary>
    /// <remarks>
    /// An observable list rather than a snapshot, because bookmarking the current directory
    /// has to appear in the rail of every open window at once — they all read the one file.
    /// </remarks>
    public AvaloniaList<PlaceViewModel> Bookmarks { get; } = [];

    /// <summary>Whether the current directory is already bookmarked, for the menu's checkmark.</summary>
    public bool IsBookmarked =>
        _state.Bookmarks.Any(b => string.Equals(b.Uri, Pane.Location.ToUriString(), StringComparison.Ordinal));

    /// <summary>Bookmarks the current directory, or removes it if it is already there.</summary>
    public void ToggleBookmarkHere()
    {
        var here = Pane.Location;
        if (!_state.AddBookmark(here, here.Name is { Length: > 0 } name ? name : here.Path))
            _state.RemoveBookmark(here);
        BookmarksChanged?.Invoke();
    }

    /// <summary>Adds a bookmark for a location that was dragged onto the rail.</summary>
    public bool AddBookmark(Location location)
    {
        var added = _state.AddBookmark(location,
            location.Name is { Length: > 0 } name ? name : location.Path);
        if (added)
            BookmarksChanged?.Invoke();
        return added;
    }

    /// <summary>Raised when this window changed the bookmarks; the application tells the rest.</summary>
    public event Action? BookmarksChanged;

    /// <summary>Re-reads the bookmark list into the rail.</summary>
    public void RefreshBookmarks()
    {
        Bookmarks.Clear();
        foreach (var bookmark in _state.Bookmarks)
        {
            if (!Location.TryParse(bookmark.Uri, out var location))
                continue;
            Bookmarks.Add(new PlaceViewModel(
                bookmark.Label is { Length: > 0 } label ? label : location.Name, location));
        }
        this.RaisePropertyChanged(nameof(IsBookmarked));
        this.RaisePropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>Whether the Bookmarks section is worth a heading.</summary>
    public bool HasBookmarks => Bookmarks.Count > 0;

    /// <summary>
    /// How wide the rail is, in pixels.
    /// </summary>
    /// <remarks>
    /// Set by the view when the splitter is let go, rather than continuously while it is
    /// dragged: the session file would otherwise be rewritten on every frame of the drag.
    /// </remarks>
    public double SidebarWidth
    {
        get => _sidebarWidth;
        set
        {
            if (Math.Abs(_sidebarWidth - value) < 0.5)
                return;
            _sidebarWidth = value;
            this.RaisePropertyChanged();
            SessionChanged?.Invoke();
        }
    }

    /// <summary>The mount table, for the free-space figure in the status bar.</summary>
    public MountTable Mounts { get; }

    /// <summary>Renders the icons the listing shows. The view asks per realized row.</summary>
    public IconService Icons { get; }

    /// <summary>Renders the previews the listing shows, for files that can have one.</summary>
    public ThumbnailService Thumbnails { get; }

    /// <summary>Whether files show a picture of themselves instead of a type icon.</summary>
    /// <remarks>
    /// Application-wide, like the other preferences, and applied to the open listings without
    /// re-reading them: turning it off drops the pictures and leaves the icons that were
    /// already there, and turning it on asks for pictures for the rows currently on screen.
    /// </remarks>
    public bool ThumbnailsEnabled
    {
        get => _state.Preferences.ThumbnailsEnabled;
        set
        {
            if (_state.Preferences.ThumbnailsEnabled == value)
                return;
            _state.Preferences = _state.Preferences with { ThumbnailsEnabled = value };
            Thumbnails.Enabled = value;
            if (!value)
            {
                foreach (var tab in Tabs)
                {
                    foreach (var row in tab.Pane.Entries)
                        row.ClearThumbnail();
                }
            }
            PreferencesChanged?.Invoke();
            VisualsInvalidated?.Invoke();
        }
    }

    /// <summary>Asks the view to re-request icons and previews for the rows it has realized.</summary>
    /// <remarks>
    /// The listing asks per container as it is realized, so a setting that changes what a
    /// realized row should show has no other way to reach the rows already on screen.
    /// </remarks>
    public event Action? VisualsInvalidated;

    /// <summary>
    /// Asks the listing to select everything.
    /// </summary>
    /// <remarks>
    /// An event rather than something this could do itself: selection lives in the control, and
    /// the view model is told what it is rather than owning it -- see <see cref="Selection"/>.
    /// Setting it from here would be the second truth that comment exists to prevent.
    /// </remarks>
    public event Action? SelectAllRequested;

    /// <summary>The application's operation queue. Shared, so a copy outlives this window.</summary>
    public OperationQueue Operations { get; }

    /// <summary>What Cut or Copy set aside. Shared across windows.</summary>
    public FileClipboard Clipboard { get; }

    /// <summary>
    /// What the listing has selected. Set by the view, because selection lives in the
    /// control and mirroring it into the view model would mean keeping two truths in step.
    /// </summary>
    public IReadOnlyList<FileEntryViewModel> Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            // Kept on the tab as well, because the TabControl rebuilds the listing on every
            // switch and the control's own copy does not survive it.
            _activeTab.Selection = value;
            this.RaisePropertyChanged(nameof(HasOpenableSelection));
        }
    }

    /// <summary>The running operation's status line, or null when nothing is running.</summary>
    public string? OperationStatus
    {
        get => _operationStatus;
        private set => this.RaiseAndSetIfChanged(ref _operationStatus, value);
    }

    /// <summary>How far the running operation has got, 0 to 1, or null while scanning.</summary>
    public double? OperationFraction
    {
        get => _operationFraction;
        private set => this.RaiseAndSetIfChanged(ref _operationFraction, value);
    }

    public bool HasOperation => _running is not null;

    public ReactiveCommand<Unit, Unit> GoBack { get; }
    public ReactiveCommand<Unit, Unit> GoForward { get; }
    public ReactiveCommand<Unit, Unit> GoUp { get; }
    public ReactiveCommand<Unit, Unit> GoHome { get; }
    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> About { get; }
    public ReactiveCommand<Unit, Unit> Close { get; }
    public ReactiveCommand<Unit, Unit> CopySelection { get; }
    public ReactiveCommand<Unit, Unit> CutSelection { get; }
    public ReactiveCommand<Unit, Unit> Paste { get; }
    public ReactiveCommand<Unit, Unit> RenameSelection { get; }
    public ReactiveCommand<Unit, Unit> MakeCopy { get; }
    public ReactiveCommand<Unit, Unit> MakeReference { get; }
    public ReactiveCommand<Unit, Unit> NewFolder { get; }
    public ReactiveCommand<Unit, Unit> TrashSelection { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelection { get; }
    public ReactiveCommand<Unit, Unit> NewTab { get; }

    /// <summary>Opens what is selected, as a double-click would.</summary>
    public ReactiveCommand<Unit, Unit> OpenSelection { get; }

    /// <summary>
    /// Opens what is selected in tabs of its own.
    /// </summary>
    /// <remarks>
    /// Only a directory can be in a tab, and <see cref="OpenAsync"/> already ignores the intent
    /// for anything else -- a file goes to its handler however it was asked for. So a mixed
    /// selection does the sensible thing without this having to sort it out.
    /// </remarks>
    public ReactiveCommand<Unit, Unit> OpenSelectionInNewTab { get; }

    /// <summary>Selects every row of the listing.</summary>
    public ReactiveCommand<Unit, Unit> SelectAll { get; }
    public ReactiveCommand<Unit, Unit> ToggleBookmark { get; }
    public ReactiveCommand<Unit, Unit> ToggleDefaultFileManager { get; }
    public ReactiveCommand<Unit, Unit> ConnectToServer { get; }
    public ReactiveCommand<Unit, Unit> Disconnect { get; }
    public ReactiveCommand<Unit, Unit> CloseTab { get; }
    public ReactiveCommand<Unit, Unit> NextTab { get; }
    public ReactiveCommand<Unit, Unit> PreviousTab { get; }

    /// <summary>Searches the directory being shown, and everything under it.</summary>
    public ReactiveCommand<Unit, Unit> Find { get; }

    /// <summary>Shows a second pane beside this one, or takes it away again.</summary>
    public ReactiveCommand<Unit, Unit> ToggleSplit { get; }

    /// <summary>Properties of the selection, or of the directory when nothing is selected.</summary>
    public ReactiveCommand<Unit, Unit> ShowProperties { get; }

    /// <summary>Properties of the directory being shown, whatever is selected inside it.</summary>
    public ReactiveCommand<Unit, Unit> ShowFolderProperties { get; }

    // The view owns every Avalonia dialog type; the view model only says what it wants
    // shown. Same contract Wlrix.Archiver uses, and what keeps these testable off the
    // dispatcher.
    public event Action<string>? ErrorRaised;
    public event Action? AboutRequested;
    public event Action? CloseRequested;

    /// <summary>Asks the view for a name. Returns null if the user canceled.</summary>
    public event Func<string, string, string, bool, Task<string?>>? PromptRequested;

    /// <summary>Asks the view for a yes or no.</summary>
    public event Func<string, string, Task<bool>>? ConfirmRequested;

    /// <summary>Asks the view what to do about a conflict.</summary>
    public event Func<ConflictContext, Task<ConflictDecision>>? ConflictRequested;

    /// <summary>Asks the view where to connect. Null if the user canceled.</summary>
    public event Func<Task<ConnectRequest?>>? ConnectRequested;

    /// <summary>Asks the view to put a properties window on screen.</summary>
    /// <remarks>
    /// The view model builds the model and the view owns the window, as everywhere else here.
    /// Unlike the other four this one answers nothing: the window is modeless, so there is no
    /// result to wait for.
    /// </remarks>
    public event Action<PropertiesViewModel>? PropertiesRequested;

    // --- remote shares ----------------------------------------------------

    /// <summary>The saved shares, for the Internet menu.</summary>
    public AvaloniaList<PlaceViewModel> Shares { get; } = [];

    /// <summary>Suspends background polling in every tab. Set when the window loses focus.</summary>
    public bool Suspended
    {
        set
        {
            foreach (var tab in Tabs)
                tab.Pane.Suspended = value;
        }
    }

    /// <summary>Whether the current pane is looking at a remote location.</summary>
    public bool IsRemote => !Pane.Location.IsLocal;

    private async Task ConnectAsync()
    {
        if (ConnectRequested is null)
            return;
        var request = await ConnectRequested().ConfigureAwait(true);
        if (request is null)
            return;

        // The credential goes to the store before the navigation, so the mount that the
        // navigation triggers finds it there rather than prompting again for what was just
        // typed. Saved whether or not "remember" was ticked -- the transient store is where an
        // unticked one lands, and it is what keeps a session from asking per directory.
        var share = ShareRef.For(request.Location, request.Credentials.Username);
        await _credentials.SaveAsync(share, request.Credentials).ConfigureAwait(true);

        if (request.Remember)
        {
            _state.SaveShare(new SavedShare
            {
                Scheme = request.Location.Scheme,
                Host = request.Location.Host ?? string.Empty,
                Port = request.Location.Port,
                Share = share.Share,
                Username = request.Credentials.Username,
                Anonymous = request.Credentials.Anonymous,
                Label = request.Location.Host ?? request.Location.Scheme
            });
            RefreshShares();
            SharesChanged?.Invoke();
        }

        await NavigateActiveTabAsync(request.Location).ConfigureAwait(true);
    }

    /// <summary>Closes the connection the current tab is using, and goes home.</summary>
    /// <remarks>
    /// Dropping the mount rather than only navigating away: the point of Disconnect is to stop
    /// holding a connection open, and a mount left in the provider would keep one until the
    /// idle timeout noticed.
    /// </remarks>
    private async Task DisconnectAsync()
    {
        var location = Pane.Location;
        if (location.IsLocal)
            return;

        await NavigateActiveTabAsync(Home).ConfigureAwait(true);
        await _provider.RemoveAsync(location.MountKey).ConfigureAwait(true);
    }

    /// <summary>Raised when this window changed the saved shares.</summary>
    public event Action? SharesChanged;

    /// <summary>Re-reads the saved share list into the Internet menu.</summary>
    public void RefreshShares()
    {
        Shares.Clear();
        foreach (var saved in _state.Shares)
        {
            var path = saved.Share.Length > 0 ? "/" + saved.Share : "/";
            var port = saved.Port < 0 ? string.Empty : ":" + saved.Port.ToString(CultureInfo.InvariantCulture);
            if (!Location.TryParse($"{saved.Scheme}://{saved.Host}{port}{path}", out var location))
                continue;
            Shares.Add(new PlaceViewModel(
                saved.Label is { Length: > 0 } label ? label : location.MountKey, location));
        }
        this.RaisePropertyChanged(nameof(HasShares));
    }

    /// <summary>Whether the Internet menu has anything saved to list.</summary>
    public bool HasShares => Shares.Count > 0;

    /// <summary>Opens a saved share, prompting for its password if the store has none.</summary>
    public Task OpenShareAsync(PlaceViewModel share) => NavigateActiveTabAsync(share.Location);

    /// <summary>Shows a message the view itself produced.</summary>
    /// <remarks>
    /// The view raises very little on its own, but a drag that cannot be staged fails in the
    /// view — it is the view that starts one — and the error has to reach the same dialog as
    /// every other one rather than being swallowed.
    /// </remarks>
    public void RaiseError(string message) => ErrorRaised?.Invoke(message);

    /// <summary>The applications that could open the selection, for the Open With menu.</summary>
    public AvaloniaList<HandlerViewModel> OpenWith { get; } = [];

    /// <summary>Whether the Open With submenu has anything in it.</summary>
    public bool HasHandlers => OpenWith.Count > 0;

    /// <summary>
    /// Whether anything is selected that an application could open.
    /// </summary>
    /// <remarks>
    /// What the Open With items are enabled by, rather than <see cref="HasHandlers"/> — and
    /// that is a fix rather than a preference. The list is filled when the menu opens, so
    /// gating on whether it is filled meant the item was disabled, a disabled item cannot be
    /// opened, the fill never ran, and it stayed disabled for ever. This answer needs no disk
    /// read and is right the moment the selection changes.
    ///
    /// <para>
    /// The cost is that a file whose type nothing handles opens an empty submenu. That is a
    /// visible, reportable symptom; a permanently grayed-out item looks deliberate.
    /// </para>
    /// </remarks>
    public bool HasOpenableSelection => Selection.Any(row => !row.IsDirectory);

    /// <summary>
    /// Rebuilds the Open With list for what is selected now.
    /// </summary>
    /// <remarks>
    /// Rebuilt when the menu is about to open rather than on every selection change: it reads
    /// mimeapps.list from disk, and a directory being rubber-banded through would do that on
    /// every mouse move.
    ///
    /// <para>
    /// The type is sniffed, the same as a double-click sniffs it. If the two disagreed, the
    /// menu would offer applications for a file that opening it would treat as something else
    /// — and the extensionless files this matters for are exactly the ones whose Open With
    /// list would otherwise be empty.
    /// </para>
    /// </remarks>
    public async Task RefreshHandlersAsync()
    {
        OpenWith.Clear();
        this.RaisePropertyChanged(nameof(HasHandlers));

        if (Selection.FirstOrDefault(row => !row.IsDirectory) is not { } row)
            return;

        var mimeType = await ContentSniffer
            .ResolveAsync(_mime, _provider, row.Entry, CancellationToken.None)
            .ConfigureAwait(true);

        // The selection can have moved on while the head of a file was read off a share.
        if (!Selection.Contains(row))
            return;

        foreach (var entry in _handlers.For(mimeType))
            OpenWith.Add(new HandlerViewModel(entry, mimeType));
        this.RaisePropertyChanged(nameof(HasHandlers));
    }

    /// <summary>Opens the selected files with a particular application.</summary>
    /// <param name="makeDefault">
    /// Whether this also becomes what a double-click uses. The "always" half of Open With,
    /// which is a user-scope write to mimeapps.list and nothing more.
    /// </param>
    public void OpenWithHandler(HandlerViewModel handler, bool makeDefault = false)
    {
        var files = Selection.Where(row => !row.IsDirectory).Select(row => row.Location).ToList();
        if (files.Count == 0)
            return;

        if (makeDefault)
        {
            _handlers.SetDefault(handler.MimeType, handler.Entry);
            _ = RefreshHandlersAsync();
        }
        _launcher.Open(handler.Entry, files);
    }

    /// <summary>
    /// Opens whatever a row points at: a directory routes, a file runs its handler.
    /// </summary>
    /// <remarks>
    /// Through the router rather than straight to the pane, which is the whole of what makes
    /// Classic mode work: the same double-click navigates in Modern and opens a window in
    /// Classic, and neither this method nor the view knows which.
    /// </remarks>
    public async Task OpenAsync(FileEntryViewModel row, OpenIntent intent = OpenIntent.Default)
    {
        if (row.IsDirectory)
        {
            _routing.Open(row.Location, intent, this);
            return;
        }

        // Sniffed, not merely named. This is one file, chosen on purpose, and it is the moment
        // where getting the type wrong is most visible: a README or a script with no extension
        // is a generic blob by name alone, and nothing offers to open a generic blob. The read
        // only happens when the name has already failed to answer.
        var mimeType = await ContentSniffer
            .ResolveAsync(_mime, _provider, row.Entry, CancellationToken.None)
            .ConfigureAwait(true);

        if (_handlers.DefaultFor(mimeType) is { } application)
        {
            _launcher.Open(application, [row.Location]);
            return;
        }

        // Nothing claims the type. Saying so is better than a double-click that appears to do
        // nothing whatever, which is what this did before.
        ErrorRaised?.Invoke(Strings.NoHandler(row.Name, mimeType));
    }

    /// <summary>Opens every selected row.</summary>
    /// <remarks>
    /// Sequentially rather than all at once: each one may sniff a file's type and start a
    /// handler, and a dozen processes launching in parallel is how a right-click on a whole
    /// directory becomes a fork bomb of image viewers.
    /// </remarks>
    private async Task OpenSelectionAsync(OpenIntent intent)
    {
        // Copied first. Opening a directory in place replaces the listing, which replaces the
        // selection this is walking.
        foreach (var row in Selection.ToList())
            await OpenAsync(row, intent).ConfigureAwait(true);
    }

    /// <summary>Opens a location the rail names, in a tab of its own.</summary>
    /// <remarks>
    /// Through the router like every other navigation, so Classic mode still answers "that
    /// directory already has a window" rather than opening a second one.
    /// </remarks>
    public void OpenInNewTab(Location location) => _routing.Open(location, OpenIntent.NewTab, this);

    /// <summary>Navigates to a path typed or clicked in the path bar.</summary>
    public Task NavigateToPathAsync(string text) =>
        Location.TryParse(text, out var location)
            ? NavigateActiveTabAsync(location)
            : Task.CompletedTask;

    /// <summary>
    /// Sends the active tab somewhere, asking the router first.
    /// </summary>
    /// <remarks>
    /// The explicit navigations — the path bar, the places rail, Home — go through here, so
    /// Classic mode can answer with "that directory already has a window" and raise it. Back,
    /// Forward and Up do not: raising a different window in answer to a Back button would be
    /// a strange thing for a Back button to do.
    /// </remarks>
    public Task NavigateActiveTabAsync(Location location)
    {
        if (_routing.Claim(this, location) is { } holder)
        {
            _routing.Present(holder);
            return Task.CompletedTask;
        }
        return Pane.NavigateAsync(location);
    }

    /// <summary>Runs a history step, then puts the registry back in step with it.</summary>
    private async Task Stepped(Task navigation)
    {
        await navigation.ConfigureAwait(true);
        _routing.Resync(this, Pane.Location);
    }

    public Task StartAsync() => Pane.ReloadAsync();

    // --- the devices rail -----------------------------------------------------

    /// <summary>The disks, fixed ones first.</summary>
    public AvaloniaList<DeviceViewModel> Devices { get; } = [];

    public bool HasDevices => Devices.Count > 0;

    /// <summary>
    /// Rebuilds the rail from what the monitor now reports.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than by diffing. The list is a handful of rows that changes when
    /// somebody plugs something in, and matching them up to preserve object identity would be
    /// more code than it saves — the one thing worth keeping across a rebuild, how full a disk
    /// is, is re-read anyway.
    /// </remarks>
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var device in _devices.Devices)
        {
            var row = new DeviceViewModel(device);
            Devices.Add(row);
            _ = row.RefreshUsageAsync();
        }

        this.RaisePropertyChanged(nameof(HasDevices));
    }

    /// <summary>
    /// Opens a disk, mounting it first if it is not mounted.
    /// </summary>
    /// <remarks>
    /// One gesture for both, because "open this disk" is the only thing anybody wants from a
    /// row in this list and whether it happens to be mounted is the machine's business. Mount
    /// is the verb in the context menu for when it is not.
    /// </remarks>
    public async Task OpenDeviceAsync(DeviceViewModel device)
    {
        if (device.Location is { } mounted)
        {
            await NavigateActiveTabAsync(mounted).ConfigureAwait(true);
            return;
        }

        var (mountPoint, problem) = await _devices.MountAsync(device.Device).ConfigureAwait(true);
        if (problem is not null)
        {
            ErrorRaised?.Invoke(Strings.DeviceMountFailed(device.Label, problem));
            return;
        }

        if (mountPoint is not null)
            await NavigateActiveTabAsync(Location.FromLocalPath(mountPoint)).ConfigureAwait(true);
    }

    /// <summary>
    /// Unmounts a disk, having first left it if we are standing on it.
    /// </summary>
    /// <remarks>
    /// A listing open on the device is a process holding it open, so unmounting from inside it
    /// would fail with "target is busy" and the reason would be this window. Going home first
    /// is what makes the obvious gesture work.
    /// </remarks>
    public async Task UnmountDeviceAsync(DeviceViewModel device)
    {
        if (device.Location is not { } mounted)
            return;

        foreach (var tab in Tabs)
        {
            foreach (var pane in tab.Panes)
            {
                if (pane.Location == mounted || mounted.Contains(pane.Location))
                    await pane.NavigateAsync(Home).ConfigureAwait(true);
            }
        }

        if (await _devices.UnmountAsync(device.Device).ConfigureAwait(true) is { } problem)
        {
            ErrorRaised?.Invoke(Strings.DeviceUnmountFailed(device.Label, problem));
            return;
        }

        StatusRaised?.Invoke(Strings.DeviceUnmounted(device.Label));
    }

    /// <summary>Raised to say something happened that is not a failure.</summary>
    public event Action<string>? StatusRaised;

    // --- split panes --------------------------------------------------------

    /// <summary>Whether the tab being shown has two panes.</summary>
    public bool IsSplit => _activeTab.IsSplit;

    /// <summary>Rebuilds a split from a saved session.</summary>
    /// <remarks>
    /// Separate from the command because restoring is not toggling: it says where the second
    /// pane goes and which of the two was in use, neither of which a toggle has an opinion
    /// about.
    /// </remarks>
    public void RestoreSplit(TabViewModel tab, Location second, int activePane)
    {
        if (tab.Split(_newPane, second) is not { } opened)
            return;

        _ = opened.ReloadAsync();
        tab.SetActivePane(tab.Panes[Math.Clamp(activePane, 0, tab.Panes.Count - 1)]);
        if (ReferenceEquals(tab, _activeTab))
            this.RaisePropertyChanged(nameof(IsSplit));
    }

    private void SplitOrUnsplit()
    {
        if (_activeTab.IsSplit)
        {
            // The pane that goes is the inactive one, so whoever asked for this keeps the
            // directory they were working in.
            if (_activeTab.Unsplit() is { } closed)
                _ = closed.CloseAsync();
        }
        else if (_activeTab.Split(_newPane) is { } opened)
        {
            _ = opened.ReloadAsync();
        }

        this.RaisePropertyChanged(nameof(IsSplit));
        OnActivePaneChanged();
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Points the window at whichever pane is now active.
    /// </summary>
    /// <remarks>
    /// Everything the window does — Paste, Delete, New Folder, Find, the path bar, the status
    /// bar — reads <see cref="Pane"/>, so this one notification is what makes clicking the
    /// other listing redirect all of it. The selection is re-read from the pane rather than
    /// kept, or the first Delete after a click would act on what was picked out in the pane
    /// nobody is looking at any more.
    /// </remarks>
    private void OnActivePaneChanged()
    {
        this.RaisePropertyChanged(nameof(Pane));
        Selection = _activeTab.ActivePane.Selection;
        RaiseSessionChanged();
    }

    // --- tabs -------------------------------------------------------------

    /// <summary>Opens a tab at <paramref name="location"/> and switches to it.</summary>
    public TabViewModel AddTab(Location location)
    {
        var tab = new TabViewModel(this, _newPane(location));
        tab.Moved += RaiseSessionChanged;
        tab.ActivePaneChanged += OnActivePaneChanged;
        Tabs.Add(tab);
        this.RaisePropertyChanged(nameof(HasMultipleTabs));
        ActiveTab = tab;
        SessionChanged?.Invoke();
        _ = tab.Pane.ReloadAsync();
        return tab;
    }

    /// <summary>Closes a tab. Closing the last one closes the window.</summary>
    /// <remarks>
    /// The window rather than an empty frame, because a file manager window with no directory
    /// in it is not a state worth being able to reach.
    /// </remarks>
    public void CloseTabAt(TabViewModel tab)
    {
        if (!Tabs.Contains(tab))
            return;
        if (Tabs.Count == 1)
        {
            CloseRequested?.Invoke();
            return;
        }

        // Chosen before the removal, so the index still means something.
        var index = Tabs.IndexOf(tab);
        var next = Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1];

        Tabs.Remove(tab);
        tab.Moved -= RaiseSessionChanged;
        tab.ActivePaneChanged -= OnActivePaneChanged;
        foreach (var pane in tab.Panes)
            _ = pane.CloseAsync();
        tab.Dispose();
        this.RaisePropertyChanged(nameof(HasMultipleTabs));
        if (ReferenceEquals(_activeTab, tab))
            ActiveTab = next;
        SessionChanged?.Invoke();
    }

    private void CloseActiveTab() => CloseTabAt(_activeTab);

    private void RaiseSessionChanged()
    {
        SessionChanged?.Invoke();
        this.RaisePropertyChanged(nameof(IsRemote));
        // The Bookmarks checkmark is about where you are, so it changes when that does.
        this.RaisePropertyChanged(nameof(IsBookmarked));
    }

    /// <summary>Moves to the next or previous tab, wrapping around.</summary>
    private void CycleTab(int step)
    {
        if (Tabs.Count < 2)
            return;
        var index = (Tabs.IndexOf(_activeTab) + step + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    // --- operations -------------------------------------------------------

    // --- find ---------------------------------------------------------------

    /// <summary>Asks what to look for, then shows the results in place of the listing.</summary>
    /// <remarks>
    /// A prompt rather than a search bar in the chrome. IRIX's file manager asked in a dialog,
    /// and it keeps the results honest: the listing is either a directory or the answer to one
    /// stated question, and every navigation puts the directory back.
    /// </remarks>
    private async Task FindAsync()
    {
        if (PromptRequested is null)
            return;

        var where = Pane.Location.IsLocal ? Pane.Location.Path : Pane.Location.ToUriString();
        var text = await PromptRequested(Strings.FindTitle, Strings.FindPrompt(where), string.Empty, true)
            .ConfigureAwait(true);
        if (text is null)
            return;

        await Pane.SearchAsync(new SearchQuery(text)).ConfigureAwait(true);
    }

    // --- properties -------------------------------------------------------

    /// <summary>
    /// Properties of what is selected, or of the directory itself when nothing is.
    /// </summary>
    /// <remarks>
    /// Falling through to the directory rather than doing nothing: the menu item is reachable
    /// with an empty selection, and "the folder you are looking at" is the only thing it could
    /// sensibly mean. The Actions menu has its own item that always means the folder, for when
    /// something is selected and the folder is what you want.
    /// </remarks>
    private Task ShowPropertiesAsync() =>
        Selection.Count == 0
            ? ShowFolderPropertiesAsync()
            : ShowPropertiesFor([.. Selection.Select(row => row.Entry)]);

    private async Task ShowFolderPropertiesAsync()
    {
        var location = Pane.Location;
        try
        {
            var fs = await _provider.GetAsync(location, CancellationToken.None).ConfigureAwait(true);
            var stat = await fs.StatAsync(location, CancellationToken.None).ConfigureAwait(true);
            await ShowPropertiesFor([EntryFor(stat)]).ConfigureAwait(true);
        }
        catch (FileOperationException ex)
        {
            ErrorRaised?.Invoke(ex.Message);
        }
    }

    private Task ShowPropertiesFor(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count > 0)
            PropertiesRequested?.Invoke(new PropertiesViewModel(_provider, Icons, _mime, Mounts, entries));
        return Task.CompletedTask;
    }

    /// <summary>A stat, in the shape the rest of the application passes around.</summary>
    /// <remarks>
    /// A directory reached through the path bar has no listing entry of its own — it is the
    /// listing — so one is made here rather than teaching the properties window to accept two
    /// kinds of subject.
    /// </remarks>
    private static FileEntry EntryFor(FileStat stat)
    {
        // The root of a filesystem has no name. Showing the whole address is better than an
        // empty heading, and for a share it is the only thing that identifies it at all.
        var name = stat.Location.Name;
        if (name.Length == 0)
            name = stat.Location.IsLocal ? stat.Location.Path : stat.Location.ToUriString();

        return new FileEntry
        {
            Location = stat.Location,
            Name = name,
            Kind = stat.Kind,
            Size = stat.Size,
            Modified = stat.Modified,
            UnixMode = stat.UnixMode,
            SymlinkTarget = stat.SymlinkTarget,
            IsHidden = name.StartsWith('.')
        };
    }

    private IReadOnlyList<Location> SelectedLocations() => [.. Selection.Select(row => row.Location)];

    private async Task PasteAsync()
    {
        if (!Clipboard.HasContent)
            return;

        var sources = Clipboard.Locations;
        var operation = Clipboard.IsCut
            ? FileOperation.Move(sources, Pane.Location)
            : FileOperation.Copy(sources, Pane.Location);

        // Consumed before the operation finishes: the sources of a cut are about to stop
        // existing, and a second paste of them would fail on files that are already gone.
        Clipboard.ConsumeIfCut();
        await RunAsync(operation).ConfigureAwait(true);
    }

    private async Task RenameAsync()
    {
        if (Selection.Count != 1 || PromptRequested is null)
            return;

        var row = Selection[0];
        // The extension is left out of the initial selection, because renaming almost always
        // means changing the stem.
        var name = await PromptRequested(
            Strings.RenameTitle, Strings.RenamePrompt(row.Name), row.Name, false).ConfigureAwait(true);
        if (name is null || name == row.Name)
            return;
        if (row.Location.Parent is not { } parent)
            return;

        await RunAsync(FileOperation.Rename(row.Location, parent.Child(name))).ConfigureAwait(true);
    }

    /// <summary>Duplicates the selection in place — IRIX's "Make Copy".</summary>
    /// <remarks>
    /// Copying into the directory the sources are already in collides with every one of them
    /// by definition, so this is one of the two operations that must not ask: the auto-rename
    /// resolver is what turns the collision into <c>foo (copy).txt</c> rather than a dialog
    /// per file saying the obvious.
    /// </remarks>
    private Task MakeCopyAsync() =>
        Selection.Count == 0
            ? Task.CompletedTask
            : RunAsync(FileOperation.Copy(SelectedLocations(), Pane.Location), new AutoRenameResolver());

    /// <summary>Links the selection in place — IRIX's "Make Reference".</summary>
    private Task MakeReferenceAsync() =>
        Selection.Count == 0
            ? Task.CompletedTask
            : RunAsync(FileOperation.Link(SelectedLocations(), Pane.Location), new AutoRenameResolver());

    private async Task NewFolderAsync()
    {
        if (PromptRequested is null)
            return;
        var name = await PromptRequested(
            Strings.NewFolderTitle, Strings.NewFolderPrompt, Strings.NewFolderDefault, true).ConfigureAwait(true);
        if (name is null)
            return;

        await RunAsync(FileOperation.NewDirectory(Pane.Location.Child(name))).ConfigureAwait(true);
    }

    private Task TrashAsync() =>
        Selection.Count == 0 ? Task.CompletedTask : RunAsync(FileOperation.SendToTrash(SelectedLocations()));

    private async Task DeleteAsync()
    {
        if (Selection.Count == 0)
            return;

        // Permanent deletion is the one action here with no way back, so it asks unless the
        // user has said not to -- Move to Trash is the one that never asks.
        if (ConfirmDelete && ConfirmRequested is not null)
        {
            var message = Selection.Count == 1
                ? Strings.ConfirmDeleteOne(Selection[0].Name)
                : Strings.ConfirmDeleteMany(Selection.Count);
            if (!await ConfirmRequested(Strings.ConfirmDeleteTitle, message).ConfigureAwait(true))
                return;
        }

        await RunAsync(FileOperation.Delete(SelectedLocations())).ConfigureAwait(true);
    }

    // --- drag and drop ----------------------------------------------------

    /// <summary>
    /// What dropping <paramref name="sources"/> on <paramref name="target"/> would do.
    /// </summary>
    /// <remarks>
    /// Asked on every drag-over event, because the answer is the only feedback there is: the
    /// compositor gives no drag image to a client that draws no drag surface, so the effect —
    /// seen as the cursor shape — is all the user has to go on. It stays a pure question with
    /// no side effects for that reason.
    /// </remarks>
    public DropAction PreviewDrop(IReadOnlyList<Location> sources, Location target, DropModifiers modifiers) =>
        DropPolicy.Decide(sources, target, modifiers, OnOneFilesystem(sources, target)).Action;

    /// <summary>Carries out a drop.</summary>
    public Task DropAsync(IReadOnlyList<Location> sources, Location target, DropModifiers modifiers)
    {
        var plan = DropPolicy.Decide(sources, target, modifiers, OnOneFilesystem(sources, target));
        return DropPolicy.ToOperation(plan, target) is { } operation
            ? RunAsync(operation)
            : Task.CompletedTask;
    }

    /// <summary>Gives every location a local path, copying the remote ones down.</summary>
    /// <remarks>
    /// The receiving application is handed files, and a file on a share is not one. Local
    /// selections pass straight through, so the common case costs nothing.
    /// </remarks>
    public Task<IReadOnlyList<string>> StageForDragAsync(
        IReadOnlyList<Location> sources, CancellationToken cancellationToken = default) =>
        _staging.StageAsync(sources, null, cancellationToken);

    /// <summary>Whether a whole selection shares the target's filesystem.</summary>
    /// <remarks>
    /// All of them, not the first: a selection spanning two disks would otherwise be moved
    /// on the strength of one member. Where the answer is mixed, copy is the direction that
    /// loses nothing.
    /// </remarks>
    private bool OnOneFilesystem(IReadOnlyList<Location> sources, Location target)
    {
        foreach (var source in sources)
        {
            if (!Mounts.IsSameFilesystem(source, target))
                return false;
        }
        return sources.Count > 0;
    }

    /// <summary>Queues an operation, follows it, and reloads when it finishes.</summary>
    private async Task RunAsync(FileOperation operation, IConflictResolver? conflicts = null)
    {
        var queued = Operations.Enqueue(operation, conflicts ?? new ViewConflictResolver(this));
        _running = queued;
        queued.Changed += OnOperationChanged;
        RefreshOperation();

        try
        {
            var result = await queued.Completion.ConfigureAwait(true);
            if (result.Errors.Count > 0)
                ErrorRaised?.Invoke(Strings.OperationFailed(result.Errors.Count) + " " + result.Errors[0].Message);
        }
        finally
        {
            queued.Changed -= OnOperationChanged;
            _running = null;
            RefreshOperation();
            // A nudge, not a reload. The directory is watched, so the change is coming
            // anyway; this only stops a share waiting out its poll interval before showing
            // what was just pasted into it. Reloading instead would clear the listing and
            // throw away the selection and scroll position after every operation.
            await Pane.RefreshAsync().ConfigureAwait(true);
        }
    }

    private void OnOperationChanged(QueuedOperation queued) => Dispatcher.UIThread.Post(RefreshOperation);

    private void RefreshOperation()
    {
        var queued = _running;
        if (queued is null)
        {
            OperationStatus = null;
            OperationFraction = null;
        }
        else
        {
            var progress = queued.Progress;
            OperationStatus = progress.Phase == OperationPhase.Scanning
                ? Strings.OperationScanning(progress.ItemsDone)
                : Strings.Operation(queued.Operation.Kind);
            OperationFraction = progress.Fraction;
        }
        this.RaisePropertyChanged(nameof(HasOperation));
    }

    /// <summary>Cancels whatever is running.</summary>
    public void CancelOperation() => _running?.Cancel();

    /// <summary>Routes a conflict from the engine out to the window's dialog.</summary>
    /// <remarks>
    /// The engine asks on a worker thread; the dialog has to be shown on the UI thread and
    /// the worker has to wait for the answer. That is what the TaskCompletionSource is for.
    /// </remarks>
    private sealed class ViewConflictResolver(MainWindowViewModel model) : IConflictResolver
    {
        public Task<ConflictDecision> ResolveAsync(ConflictContext context, CancellationToken cancellationToken)
        {
            if (model.ConflictRequested is not { } ask)
                return Task.FromResult(ConflictDecision.Skip(all: true));

            var answer = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    answer.TrySetResult(await ask(context).ConfigureAwait(true));
                }
                catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
                {
                    // A window closing mid-operation must not leave the worker waiting.
                    answer.TrySetResult(ConflictDecision.Skip(all: true));
                }
            });
            return answer.Task.WaitAsync(cancellationToken);
        }
    }

    private static List<PlaceViewModel> BuildPlaces(XdgUserDirs userDirs)
    {
        var places = new List<PlaceViewModel>
        {
            new(Strings.Catalog.Get("PlaceHome"), userDirs.Home)
        };
        foreach (var dir in userDirs.Places())
            places.Add(new PlaceViewModel(Strings.Place(dir.Key), dir.Location));
        places.Add(new PlaceViewModel(Strings.Catalog.Get("PlaceFilesystem"), Location.FromLocalPath("/")));
        return places;
    }

    /// <summary>
    /// Wraps an async action as a command with its failures observed.
    /// </summary>
    /// <remarks>
    /// Every <c>ReactiveCommand</c> must have <c>ThrownExceptions</c> subscribed. An
    /// unobserved throw is rethrown on the scheduler and takes the process down, which is
    /// the bug the whole repo guards against this way.
    /// </remarks>
    private ReactiveCommand<Unit, Unit> Command(Func<Task> action)
    {
        var command = ReactiveCommand.CreateFromTask(action);
        _subscriptions.Add(command.ThrownExceptions.Subscribe(ex =>
        {
            _logger.ZLogError(ex, $"command failed");
            ErrorRaised?.Invoke(ex.Message);
        }));
        return command;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        foreach (var tab in Tabs)
        {
            _ = tab.Pane.CloseAsync();
            tab.Dispose();
        }
    }
}
