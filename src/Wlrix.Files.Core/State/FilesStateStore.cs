using Wlrix.Common;

namespace Wlrix.Files.Core.State;

/// <summary>
/// Everything the file manager remembers between runs.
/// </summary>
/// <remarks>
/// Five files under <c>&lt;AppData&gt;/files/</c>, and deliberately <b>not</b>
/// <c>wlrix-settings-daemon</c>. That daemon owns the wlRIX <i>configuration</i> — the files
/// the compositor and the session read, and the ones a user hand-edits. Which folder a window
/// was showing is neither, and putting it there would cost a schema entry, a validation rule,
/// a pidfile, a SIGHUP handler and a <c>--check-config</c> mode for a checkbox.
///
/// <para>
/// Two of the five will be promoted eventually: <c>NavigationMode</c> and <c>IconTheme</c> are
/// genuinely settings, and they can go across together in one go once the rest of the app has
/// stopped moving.
/// </para>
///
/// <para>
/// Nothing here throws. A read-only home directory, a corrupt file, a document from a newer
/// build — each degrades to the defaults, because every one of these is a convenience and
/// none of them is worth refusing to start over.
/// </para>
/// </remarks>
public sealed class FilesStateStore
{
    /// <summary>The schema version every document is written with.</summary>
    public const int CurrentVersion = 1;

    /// <summary>How many directories keep a remembered view.</summary>
    /// <remarks>
    /// Bounded because it is otherwise unbounded: a user who browses a source tree visits tens
    /// of thousands of directories, and a file that grows forever is one that eventually takes
    /// a noticeable moment to parse at startup.
    /// </remarks>
    public const int ViewStateCapacity = 2000;

    private readonly string _root;
    private readonly Action<string>? _warn;

    private BookmarksDocument _bookmarks;
    private SharesDocument _shares;
    private SessionDocument _session;
    private FilesPreferences _preferences;

    // Held as a dictionary rather than as the document, because this is the one collection
    // touched on every navigation. Rebuilding a 2000-element list to record one visit is the
    // kind of cost that does not show up until somebody browses a source tree.
    private readonly Dictionary<string, ViewStateEntry> _views = new(StringComparer.Ordinal);
    private long _nextUse = 1;
    private bool _viewsDirty;

    /// <summary>Reads everything. Missing files are not an error; a first run has none.</summary>
    /// <param name="root">Where the files live. Defaults to <c>&lt;AppData&gt;/files</c>.</param>
    /// <param name="warn">Where to report an unreadable file. Null discards.</param>
    public FilesStateStore(string? root = null, Action<string>? warn = null)
    {
        _root = root ?? Path.Combine(ApplicationPaths.AppData, "files");
        _warn = warn;

        _bookmarks = Read(BookmarksPath, FilesJson.Default.BookmarksDocument, new BookmarksDocument(), d => d.Version);
        _shares = Read(SharesPath, FilesJson.Default.SharesDocument, new SharesDocument(), d => d.Version);
        _session = Read(SessionPath, FilesJson.Default.SessionDocument, new SessionDocument(), d => d.Version);
        _preferences = Read(PreferencesPath, FilesJson.Default.FilesPreferences, new FilesPreferences(), d => d.Version);

        var views = Read(ViewStatePath, FilesJson.Default.ViewStateDocument, new ViewStateDocument(), d => d.Version);
        _nextUse = views.NextUse;
        foreach (var entry in views.Directories)
            _views[entry.Uri] = entry;
    }

    public string Root => _root;

    public string BookmarksPath => Path.Combine(_root, "bookmarks.json");
    public string SharesPath => Path.Combine(_root, "shares.json");
    public string ViewStatePath => Path.Combine(_root, "view-state.json");
    public string SessionPath => Path.Combine(_root, "session.json");
    public string PreferencesPath => Path.Combine(_root, "preferences.json");

    // --- preferences ------------------------------------------------------

    /// <summary>The Options menu's state. Assigning writes the file.</summary>
    public FilesPreferences Preferences
    {
        get => _preferences;
        set
        {
            _preferences = value with { Version = CurrentVersion };
            StateFile.Save(PreferencesPath, _preferences, FilesJson.Default.FilesPreferences, _warn);
        }
    }

    // --- bookmarks --------------------------------------------------------

    public IReadOnlyList<Bookmark> Bookmarks => _bookmarks.Bookmarks;

    /// <summary>Adds a bookmark at the end, or does nothing if the location is already there.</summary>
    public bool AddBookmark(Location location, string label = "", string? icon = null)
    {
        var uri = location.ToUriString();
        if (_bookmarks.Bookmarks.Any(b => string.Equals(b.Uri, uri, StringComparison.Ordinal)))
            return false;

        SetBookmarks([.. _bookmarks.Bookmarks, new Bookmark(uri, label, icon)]);
        return true;
    }

    public bool RemoveBookmark(Location location)
    {
        var uri = location.ToUriString();
        var kept = _bookmarks.Bookmarks
            .Where(b => !string.Equals(b.Uri, uri, StringComparison.Ordinal))
            .ToList();
        if (kept.Count == _bookmarks.Bookmarks.Count)
            return false;

        SetBookmarks(kept);
        return true;
    }

    /// <summary>Moves the bookmark at <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <remarks>
    /// The rail is ordered by hand, so reordering is a real operation rather than a sort.
    /// Out-of-range indices answer false instead of throwing: this is driven by a drag, and a
    /// drop that lands somewhere unexpected should do nothing.
    /// </remarks>
    public bool MoveBookmark(int from, int to)
    {
        var list = _bookmarks.Bookmarks.ToList();
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to)
            return false;

        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);
        SetBookmarks(list);
        return true;
    }

    private void SetBookmarks(IReadOnlyList<Bookmark> bookmarks)
    {
        _bookmarks = new BookmarksDocument { Bookmarks = bookmarks };
        StateFile.Save(BookmarksPath, _bookmarks, FilesJson.Default.BookmarksDocument, _warn);
    }

    // --- saved shares -----------------------------------------------------

    public IReadOnlyList<SavedShare> Shares => _shares.Shares;

    /// <summary>Saves a share, replacing any entry that names the same thing.</summary>
    public void SaveShare(SavedShare share)
    {
        var others = _shares.Shares.Where(existing => !SameShare(existing, share)).ToList();
        others.Add(share);
        SetShares(others);
    }

    public bool RemoveShare(SavedShare share)
    {
        var kept = _shares.Shares.Where(existing => !SameShare(existing, share)).ToList();
        if (kept.Count == _shares.Shares.Count)
            return false;
        SetShares(kept);
        return true;
    }

    /// <summary>
    /// Whether two entries name the same share.
    /// </summary>
    /// <remarks>
    /// The username is part of the identity: one host can be reached as two different people,
    /// and folding those together would silently discard one of the two saved connections.
    /// The label is not, because that is what the user renames.
    /// </remarks>
    private static bool SameShare(SavedShare a, SavedShare b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Share, b.Share, StringComparison.Ordinal)
        && string.Equals(a.Username, b.Username, StringComparison.Ordinal);

    private void SetShares(IReadOnlyList<SavedShare> shares)
    {
        _shares = new SharesDocument { Shares = shares };
        StateFile.Save(SharesPath, _shares, FilesJson.Default.SharesDocument, _warn);
    }

    // --- per-directory view state ----------------------------------------

    /// <summary>How many directories currently have a remembered view.</summary>
    public int RememberedDirectories => _views.Count;

    /// <summary>What this directory looked like last time, or null if it is new.</summary>
    /// <remarks>
    /// Reading counts as a use, so a directory revisited every day is not evicted in favor of
    /// one opened once — which is the whole point of bounding the file by use rather than by
    /// when the entry was written.
    /// </remarks>
    public DirectoryViewState? ViewStateFor(Location directory)
    {
        if (!_views.TryGetValue(directory.ToUriString(), out var found))
            return null;

        Touch(found.Uri, found.State);
        return found.State;
    }

    /// <summary>Remembers how a directory was left, evicting the least recently used if full.</summary>
    /// <remarks>
    /// Held in memory until <see cref="Flush"/>. Writing on every navigation would mean
    /// serializing two thousand entries to record that one directory was visited, on the UI
    /// thread, each time the user clicked a folder.
    /// </remarks>
    public void RememberViewState(Location directory, DirectoryViewState state) =>
        Touch(directory.ToUriString(), state);

    private void Touch(string uri, DirectoryViewState state)
    {
        _views[uri] = new ViewStateEntry(uri, _nextUse++, state);
        _viewsDirty = true;

        if (_views.Count <= ViewStateCapacity)
            return;

        // Only once full, and then once per addition, so the linear scan for the oldest is
        // paid at the steady state rather than on the way to it.
        foreach (var stale in _views.Values
                     .OrderBy(entry => entry.Used)
                     .Take(_views.Count - ViewStateCapacity)
                     .ToList())
        {
            _views.Remove(stale.Uri);
        }
    }

    /// <summary>Writes the remembered views, if any have changed.</summary>
    /// <remarks>
    /// Called on a debounce and again at shutdown, the same treatment the session gets. A
    /// crash between the two loses which view a few directories were left in, which is the
    /// right thing to lose.
    /// </remarks>
    public void Flush()
    {
        if (!_viewsDirty)
            return;

        var document = new ViewStateDocument
        {
            NextUse = _nextUse,
            Directories = [.. _views.Values.OrderByDescending(entry => entry.Used)]
        };
        if (StateFile.Save(ViewStatePath, document, FilesJson.Default.ViewStateDocument, _warn))
            _viewsDirty = false;
    }

    // --- the session ------------------------------------------------------

    /// <summary>The windows and tabs the last run left open.</summary>
    public IReadOnlyList<WindowSession> Session => _session.Windows;

    /// <summary>Records the open windows. Called debounced, and once more on shutdown.</summary>
    public void SaveSession(IReadOnlyList<WindowSession> windows)
    {
        _session = new SessionDocument { Windows = windows };
        StateFile.Save(SessionPath, _session, FilesJson.Default.SessionDocument, _warn);
    }

    private T Read<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        T fallback,
        Func<T, int> versionOf)
        where T : class =>
        StateFile.Load(path, typeInfo, fallback, versionOf, CurrentVersion, _warn);
}
