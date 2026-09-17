using Avalonia.Collections;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wlrix.Files.Core;
using Wlrix.Files.Core.Listing;
using Wlrix.Files.Core.Search;
using Wlrix.Files.Core.State;
using Wlrix.Files.Core.Watching;
using Wlrix.Files.Localization;
using ZLogger;

namespace Wlrix.Files.ViewModels;

/// <summary>
/// One directory view: where it is, what is in it, and how to get somewhere else.
/// </summary>
/// <remarks>
/// The pane owns the navigation history and the listing. A window holds one today and will
/// hold several once tabs and split panes land, which is why this is separate from the window
/// rather than folded into it.
/// </remarks>
public sealed class PaneViewModel : ReactiveObject
{
    private readonly FileSystemProvider _provider;
    private readonly ILogger<PaneViewModel> _logger;

    // Back and forward as two stacks, which is what makes the browser semantics fall out:
    // navigating pushes onto back and clears forward; going back moves the current location
    // across to forward.
    private readonly Stack<Location> _back = new();
    private readonly Stack<Location> _forward = new();

    private CancellationTokenSource? _loading;
    private IDirectoryWatcher? _watcher;
    private Location _location;
    private string _status = string.Empty;
    private bool _isLoading;
    private SortKey _sortKey = SortKey.Name;
    private bool _sortDescending;
    private bool _foldersFirst = true;
    private bool _showHidden;
    private ViewMode _viewMode = ViewMode.Icons;

    /// <summary>The search being shown, or null when this is an ordinary directory listing.</summary>
    private SearchQuery? _search;

    public PaneViewModel(FileSystemProvider provider, ILogger<PaneViewModel> logger, Location start)
    {
        _provider = provider;
        _logger = logger;
        _location = start;
    }

    /// <summary>The tab this pane sits in, once it has been put in one.</summary>
    /// <remarks>
    /// Set by the tab rather than passed in, because a pane is created before there is a tab to
    /// hold it — and because a pane can be moved into a split without being rebuilt. Null in
    /// the tests, which is the point of it being a property.
    /// </remarks>
    public TabViewModel? Tab { get; set; }

    /// <summary>
    /// What is selected here, kept while the tab is switched away from.
    /// </summary>
    /// <remarks>
    /// On the pane rather than the tab, because a split tab has two listings and each keeps its
    /// own. Avalonia's <c>TabControl</c> has one content presenter and rebuilds it on every
    /// switch, so the controls are gone by the time you come back; holding the selection here
    /// means switching away and back does not silently drop what you had picked out, which with
    /// Cut and Delete a menu away is worth more than the code costs.
    /// </remarks>
    public IReadOnlyList<FileEntryViewModel> Selection { get; set; } = [];

    /// <summary>Whether this is the pane the window's commands act on.</summary>
    /// <remarks>
    /// Shown only when a tab is split, because with one pane the answer is never in doubt and a
    /// highlight around the only listing would be noise. When it is split it is essential:
    /// Paste and Delete go somewhere, and which somewhere has to be visible before the click
    /// rather than discovered after it.
    /// </remarks>
    public bool IsActive => Tab is null || ReferenceEquals(Tab.ActivePane, this);

    /// <summary>Makes this the pane the window acts on.</summary>
    public void Activate() => Tab?.SetActivePane(this);

    /// <summary>Whether to draw the active-pane outline around this listing.</summary>
    /// <remarks>
    /// Active <em>and</em> split. With one pane the answer is never in doubt, and an outline
    /// around the only listing in the window would be decoration that has to be explained.
    /// </remarks>
    public bool ShowActiveOutline => Tab is { IsSplit: true } tab && ReferenceEquals(tab.ActivePane, this);

    /// <summary>Told by the tab when the active pane changed, so the highlight can follow.</summary>
    internal void RaiseActiveChanged()
    {
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(ShowActiveOutline));
    }

    /// <summary>
    /// The rows, in display order.
    /// </summary>
    /// <remarks>
    /// An <see cref="AvaloniaList{T}"/> rather than an <c>ObservableCollection</c>, and that
    /// is load-bearing: each arriving batch is one <c>AddRange</c> raising a single ranged
    /// change notification. A hundred thousand individual Add notifications through
    /// <c>ItemsSourceView</c> is the failure mode this design exists to avoid.
    /// </remarks>
    public AvaloniaList<FileEntryViewModel> Entries { get; } = [];

    /// <summary>Raised when the listing is about to be replaced, so pending work can be dropped.</summary>
    public event Action? Invalidated;

    /// <summary>
    /// Whether the directory is being polled rather than watched, and may pause.
    /// </summary>
    /// <remarks>
    /// A share is re-read on a timer, and a window nobody is looking at should not spend the
    /// afternoon talking to a server. Local directories are event-driven and cost nothing
    /// while idle, so they are never paused.
    /// </remarks>
    public bool Suspended
    {
        set
        {
            if (_watcher is PollingWatcher polling)
                polling.Paused = value;
        }
    }

    /// <summary>Asks the watcher to look now rather than at the next interval.</summary>
    /// <remarks>
    /// What a finished operation calls. A paste onto a share would otherwise sit invisible
    /// for up to the poll interval, which is long enough to look broken.
    /// </remarks>
    public Task RefreshAsync() =>
        _watcher?.RefreshAsync(CancellationToken.None) ?? Task.CompletedTask;

    /// <summary>
    /// Folds a batch of changes into the listing rather than re-reading it.
    /// </summary>
    /// <remarks>
    /// Incremental because a reload is not free and is not invisible: it clears the list, so a
    /// file appearing in a directory you are working in would throw away your selection and
    /// your scroll position. Arrives from a watcher thread, so it hops to the dispatcher.
    /// </remarks>
    private void Apply(IReadOnlyList<DirectoryChange> changes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var comparer = Comparer(_sortKey, _sortDescending, _foldersFirst);
            var touched = false;

            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case DirectoryChangeKind.Removed:
                        touched |= Remove(change.Location);
                        break;

                    case DirectoryChangeKind.Changed:
                        // Removed and reinserted rather than mutated: a row is immutable by
                        // design, and its size and date are formatted once in the constructor.
                        // It also has to move if the sort is by whatever just changed.
                        Remove(change.Location);
                        touched |= Insert(change.Entry, comparer);
                        break;

                    default:
                        touched |= Insert(change.Entry, comparer);
                        break;
                }
            }

            if (touched)
                Status = Entries.Count == 0 ? Strings.StatusEmpty : Strings.StatusItems(Entries.Count);
        });
    }

    private bool Remove(Location location)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Location != location)
                continue;
            Entries.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>Puts a new row where the current sort says it belongs.</summary>
    /// <remarks>
    /// A binary search rather than appending and re-sorting: appending would put the file at
    /// the bottom until something else prompted a sort, and re-sorting a hundred thousand rows
    /// because one file arrived is the cost this whole path exists to avoid.
    /// </remarks>
    private bool Insert(FileEntry? entry, Comparison<FileEntryViewModel> comparer)
    {
        if (entry is null || (!_showHidden && entry.IsHidden))
            return false;

        var row = new FileEntryViewModel(entry);
        var low = 0;
        var high = Entries.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (comparer(Entries[middle], row) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        Entries.Insert(low, row);
        return true;
    }

    /// <summary>Where this pane is looking.</summary>
    public Location Location
    {
        get => _location;
        private set => this.RaiseAndSetIfChanged(ref _location, value);
    }

    /// <summary>The path as the path bar shows it.</summary>
    public string PathText => Location.IsLocal ? Location.Path : Location.ToUriString();

    /// <summary>
    /// The unsplittable leading crumb, for a remote location. Null when local.
    /// </summary>
    public string? PathRoot => Location.IsLocal ? null : Location.MountKey;

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public bool CanGoUp => Location.Parent is not null;

    /// <summary>
    /// Icons or details. Icons is the default because it is what IRIX's fm opened with.
    /// </summary>
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value)
                return;
            this.RaiseAndSetIfChanged(ref _viewMode, value);
            this.RaisePropertyChanged(nameof(IsIconView));
            this.RaisePropertyChanged(nameof(IsDetailsView));
        }
    }

    public bool IsIconView => _viewMode == ViewMode.Icons;

    public bool IsDetailsView => _viewMode == ViewMode.Details;

    public SortKey SortKey
    {
        get => _sortKey;
        set
        {
            if (_sortKey == value)
                return;
            this.RaiseAndSetIfChanged(ref _sortKey, value);
            Resort();
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (_sortDescending == value)
                return;
            this.RaiseAndSetIfChanged(ref _sortDescending, value);
            Resort();
        }
    }

    public bool FoldersFirst
    {
        get => _foldersFirst;
        set
        {
            if (_foldersFirst == value)
                return;
            this.RaiseAndSetIfChanged(ref _foldersFirst, value);
            Resort();
        }
    }

    /// <summary>
    /// Whether dotfiles are shown. Toggling re-reads rather than filtering in place, which is
    /// simpler and imperceptible next to the cost of the directory read itself.
    /// </summary>
    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden == value)
                return;
            this.RaiseAndSetIfChanged(ref _showHidden, value);
            _ = ReloadAsync();
        }
    }

    /// <summary>Raised when a load fails, for the view to put in front of the user.</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>Whether the list is search results rather than a directory.</summary>
    public bool IsSearching => _search is not null;

    /// <summary>
    /// Replaces the listing with what matches <paramref name="query"/> under this directory.
    /// </summary>
    /// <remarks>
    /// The results go in the listing rather than in a window of their own, which is what makes
    /// every one of them behave like a file: opening, copying, renaming and properties all work
    /// on a result because a result is an ordinary row pointing at an ordinary location.
    /// </remarks>
    public Task SearchAsync(SearchQuery query)
    {
        _search = query;
        this.RaisePropertyChanged(nameof(IsSearching));
        return ReloadAsync();
    }

    /// <summary>Drops the results and shows the directory again.</summary>
    private void ClearSearch()
    {
        if (_search is null)
            return;
        _search = null;
        this.RaisePropertyChanged(nameof(IsSearching));
    }

    /// <summary>Goes to <paramref name="target"/>, recording where we were.</summary>
    public Task NavigateAsync(Location target)
    {
        if (target == Location)
        {
            // Going to where you already are, while results are showing, means "give me the
            // directory back" — nobody clicks the current directory to re-run a search.
            ClearSearch();
            return ReloadAsync();
        }

        _back.Push(Location);
        _forward.Clear();
        return GoAsync(target);
    }

    public Task GoBackAsync() => _back.Count == 0
        ? Task.CompletedTask
        : Step(_back, _forward);

    public Task GoForwardAsync() => _forward.Count == 0
        ? Task.CompletedTask
        : Step(_forward, _back);

    public Task GoUpAsync() => Location.Parent is { } parent ? NavigateAsync(parent) : Task.CompletedTask;

    private Task Step(Stack<Location> from, Stack<Location> to)
    {
        to.Push(Location);
        return GoAsync(from.Pop());
    }

    /// <summary>Points a pane at a directory with no history behind it.</summary>
    /// <remarks>
    /// What a freshly created window or tab does. Navigating there instead would leave the
    /// directory it was constructed at sitting in the Back stack, so a new window would open
    /// with a Back button that goes somewhere the user never was.
    /// </remarks>
    public void ResetTo(Location target)
    {
        _back.Clear();
        _forward.Clear();
        Location = target;
        this.RaisePropertyChanged(nameof(PathText));
        this.RaisePropertyChanged(nameof(PathRoot));
        RaiseNavigationState();
    }

    private Task GoAsync(Location target)
    {
        // Every navigation funnels through here, so this is the one place search results have
        // to be dropped. Leaving them up while the location changed underneath would be a list
        // that no longer has anything to do with the path bar above it.
        ClearSearch();
        Location = target;
        this.RaisePropertyChanged(nameof(PathText));
        this.RaisePropertyChanged(nameof(PathRoot));
        RaiseNavigationState();
        return ReloadAsync();
    }

    private void RaiseNavigationState()
    {
        this.RaisePropertyChanged(nameof(CanGoBack));
        this.RaisePropertyChanged(nameof(CanGoForward));
        this.RaisePropertyChanged(nameof(CanGoUp));
    }

    /// <summary>Re-reads the current directory.</summary>
    public async Task ReloadAsync()
    {
        // Starting a second read abandons the first. Without this, clicking quickly through
        // a few directories interleaves their batches into one list.
        var previous = _loading;
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(true);
            previous.Dispose();
        }

        // The old directory's watcher goes before the new listing starts, or it would keep
        // reporting changes against entries that are no longer on screen.
        await StopWatchingAsync().ConfigureAwait(true);

        Entries.Clear();
        IsLoading = true;
        Status = _search is null ? Strings.StatusLoading : Strings.StatusSearching(0);
        // Whatever was queued was for the directory we have just left.
        Invalidated?.Invoke();

        var arrived = new List<FileEntryViewModel>();
        try
        {
            var fs = await _provider.GetAsync(Location, token).ConfigureAwait(true);
            var outcome = default(SearchOutcome);
            var listing = _search is { } query
                // Show Hidden governs a search too, rather than the query carrying its own
                // answer that the View menu could then contradict.
                ? new DirectoryListing(
                    Location,
                    inner => FileSearch.RunAsync(
                        _provider,
                        Location,
                        query with { IncludeHidden = _showHidden },
                        found => outcome = found,
                        inner))
                : new DirectoryListing(fs, Location);

            await foreach (var batch in listing.ReadAsync(token).ConfigureAwait(true))
            {
                var rows = new List<FileEntryViewModel>(batch.Entries.Count);
                foreach (var entry in batch.Entries)
                {
                    if (!_showHidden && entry.IsHidden)
                        continue;
                    var row = new FileEntryViewModel(entry);
                    if (_search is not null)
                        row.DisplayName = RelativeTo(entry.Location, Location);
                    rows.Add(row);
                }

                arrived.AddRange(rows);

                // Appended in arrival order so the first screenful appears immediately; the
                // sort happens once at the end. Inserting into a sorted list as entries
                // arrive is quadratic and shows as a stall on a large directory.
                Entries.AddRange(rows);
                Status = _search is null
                    ? Strings.StatusItems(arrived.Count)
                    : Strings.StatusSearching(arrived.Count);
            }

            if (listing.Error is { } error)
            {
                _logger.ZLogWarning($"listing failed: {error.Kind} at {listing.Directory}");
                ErrorRaised?.Invoke(error.Message);
            }

            if (token.IsCancellationRequested)
                return;

            ApplySort(arrived);
            Entries.Clear();
            Entries.AddRange(arrived);

            if (_search is { } finished)
            {
                Status = Describe(finished, arrived.Count, outcome);
                // No watcher: results are not a directory, and there is nothing coherent for
                // one to report against a list gathered from a whole tree.
                return;
            }

            Status = arrived.Count == 0 ? Strings.StatusEmpty : Strings.StatusItems(arrived.Count);
            await StartWatchingAsync(fs, listing).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later navigation. The newer read owns the list now.
        }
        catch (FileOperationException ex)
        {
            _logger.ZLogWarning($"could not read {Location}: {ex.Kind}");
            Status = string.Empty;
            ErrorRaised?.Invoke(ex.Message);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    /// <summary>Where a result is, relative to what was searched.</summary>
    /// <remarks>
    /// The display name and nothing more: the row still carries the real name and the real
    /// location, so renaming, opening and the type all keep working on a result.
    /// </remarks>
    private static string RelativeTo(Location found, Location root)
    {
        var path = found.Path;
        var prefix = root.Path.EndsWith('/') ? root.Path : root.Path + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : found.Name;
    }

    /// <summary>What the status bar says once a search has finished.</summary>
    /// <remarks>
    /// The count alone would not do. A search that stopped at its limit and a search that found
    /// exactly that many are different answers, and so is one that skipped directories it could
    /// not read — a person who searched their home directory and got nothing deserves to know
    /// whether that means "nothing there" or "could not look".
    /// </remarks>
    private static string Describe(SearchQuery query, int shown, SearchOutcome outcome)
    {
        if (outcome.Truncated)
            return Strings.StatusFoundTruncated(shown, query.Text);
        if (shown == 0)
            return Strings.StatusFoundNothing(query.Text);
        return outcome.Unreadable > 0
            ? Strings.StatusFoundPartial(shown, query.Text, outcome.Unreadable)
            : Strings.StatusFound(shown, query.Text);
    }

    /// <summary>Watches the directory just read, seeded with what was read.</summary>
    /// <remarks>
    /// Seeded, or the watcher's first look would report the entire directory as newly added.
    /// Failing to watch is not failing to browse, so nothing here can throw into the load.
    /// </remarks>
    private async Task StartWatchingAsync(IFileSystem fs, DirectoryListing listing)
    {
        try
        {
            var watcher = DirectoryWatchers.For(fs, Location);
            watcher.Changed += Apply;
            await watcher.StartAsync([.. Entries.Select(row => row.Entry)], CancellationToken.None)
                .ConfigureAwait(true);
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.ZLogWarning($"could not watch {Location}: {ex.Message}");
        }
    }

    private async Task StopWatchingAsync()
    {
        if (_watcher is not { } watcher)
            return;
        _watcher = null;
        watcher.Changed -= Apply;
        await watcher.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Stops watching. Called when the tab or the window goes.</summary>
    public Task CloseAsync() => StopWatchingAsync();

    private void Resort()
    {
        if (Entries.Count == 0)
            return;
        var rows = Entries.ToList();
        ApplySort(rows);
        Entries.Clear();
        Entries.AddRange(rows);
    }

    private void ApplySort(List<FileEntryViewModel> rows)
    {
        var comparer = Comparer(_sortKey, _sortDescending, _foldersFirst);
        rows.Sort(comparer);
    }

    /// <summary>Builds the row comparer for a sort setting.</summary>
    /// <remarks>
    /// Internal rather than private so the tests can exercise the ordering rules without a
    /// dispatcher: they are fiddly (folders first is orthogonal to direction, and a
    /// descending sort must not reverse the folder grouping) and easy to get subtly wrong.
    /// </remarks>
    internal static Comparison<FileEntryViewModel> Comparer(SortKey key, bool descending, bool foldersFirst) =>
        (a, b) =>
        {
            // Grouping is applied before direction, so reversing the order does not put the
            // files above the folders.
            if (foldersFirst && a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            var result = key switch
            {
                SortKey.Size => a.Entry.Size.CompareTo(b.Entry.Size),
                SortKey.Kind => string.Compare(a.KindText, b.KindText, StringComparison.CurrentCulture),
                SortKey.Modified => Nullable.Compare(a.Entry.Modified, b.Entry.Modified),
                _ => 0
            };

            // Name is the tie-break for every other key, so equal sizes or identical
            // timestamps still come out in a stable, readable order.
            if (result == 0)
                result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

            return descending ? -result : result;
        };
}
