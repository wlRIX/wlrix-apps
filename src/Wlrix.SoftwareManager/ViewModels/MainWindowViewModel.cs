using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Wlrix.Packages;
using Wlrix.Packages.Backends;
using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Wlrix.Packages.Privileged;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;
using ZLogger;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The window: the mode buttons, the Available Software field, and each pane's own view model.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, ITransactionObserver, IDisposable
{
    private readonly PackageSystem _system;
    private readonly ITransactionService _transactions;
    private readonly ILogger<MainWindowViewModel> _logger;

    // One listing at a time. Switching mode or pressing Lookup while a query is in flight
    // abandons it: a slow `pacman -Qi` over two thousand packages must not land in the list
    // after the user has moved on and overwrite what they asked for next.
    private CancellationTokenSource? _query;

    // Separate from the query token: stopping a transaction and abandoning a listing are
    // different things, and Stop must not cancel a refresh that happens to be in flight.
    private CancellationTokenSource? _transaction;

    // What the package manager objected to during the transaction, for the Conflicts window.
    private readonly List<TransactionConflict> _conflicts = [];

    // The commands' error subscriptions, held for the life of the view model.
    private readonly CompositeDisposable _subscriptions = [];

    // Set when the Available Software field held a path, so Start builds an installfile
    // transaction rather than looking the row's name up in a repository.
    private string? _packageFile;

    private ManagerMode _mode = ManagerMode.Install;
    private string _sourceText = string.Empty;
    private string _commandLine = string.Empty;
    private bool _hasConflicts;
    private bool _isRunning;

    public MainWindowViewModel(IPackageBackendFactory backendFactory,
        ITransactionService transactions, IPaneLayoutStore paneLayoutStore,
        IDiskSpaceProbe diskSpaceProbe, ILogger<MainWindowViewModel> logger)
    {
        _system = backendFactory.Resolve();
        _transactions = transactions;
        _logger = logger;
        Panes = new PanesViewModel(paneLayoutStore);
        DiskSpace = new DiskSpaceViewModel(diskSpaceProbe);

        // The marks live on the rows, so nothing else would tell Start that it has work.
        Inventory.MarksChanged += OnMarksChanged;

        var idle = this.WhenAnyValue(x => x.IsRunning, running => !running);

        Lookup = ReactiveCommand.Create(() => Reload(), idle);

        // Refresh re-downloads the package lists, which is a privileged transaction -- not a
        // second Lookup. It used to be wired to Reload, which meant the menu item said
        // "Refresh Package Lists" and re-ran the current search instead.
        Refresh = ReactiveCommand.Create(RunRefresh,
            this.WhenAnyValue(x => x.IsRunning, running => !running && _transactions.IsAvailable));
        Start = ReactiveCommand.Create(RunStart, this.WhenAnyValue(x => x.CanStart));
        Stop = ReactiveCommand.Create(RunStop, this.WhenAnyValue(x => x.IsRunning));
        ShowConflicts = ReactiveCommand.Create(RaiseConflicts, this.WhenAnyValue(x => x.HasConflicts));
        ShowAbout = ReactiveCommand.Create(RaiseAbout);
        ShowRepositories = ReactiveCommand.Create(() => RepositoriesRequested?.Invoke(),
            Observable.Return(_system.Backend.Capabilities.HasFlag(BackendCapabilities.ListRepositories)));

        // Always available: managing AppImages needs no package manager and no root, so this
        // works on a system where everything else in the window is greyed out.
        ShowUserSoftware = ReactiveCommand.Create(() => UserSoftwareRequested?.Invoke());

        SelectAll = ReactiveCommand.Create(() => MarkAll(install: Mode != ManagerMode.Manage));
        DeselectAll = ReactiveCommand.Create(() => Inventory.ClearMarks());
        MarkForInstall = ReactiveCommand.Create(() => MarkSelected(install: true));
        MarkForRemoval = ReactiveCommand.Create(() => MarkSelected(install: false));

        // A ReactiveCommand that throws has nowhere to put the exception, so it rethrows it on
        // the scheduler and the process goes down -- which is how a stale CancellationTokenSource
        // became a crash rather than a line in the log. Every command reports here instead.
        foreach (var command in new IHandleObservableErrors[]
                 {
                     Lookup, Refresh, Start, Stop, ShowConflicts, ShowAbout, ShowRepositories,
                     ShowUserSoftware, SelectAll, DeselectAll, MarkForInstall, MarkForRemoval,
                 })
            _subscriptions.Add(command.ThrownExceptions.Subscribe(OnCommandFailed));

        Status.Message = _system.IsSupported
            ? Strings.StatusIdle(_system.Distribution.Name, _system.Backend.Id)
            : Strings.StatusNoBackend;
    }

    /// <summary>Design-time constructor: the previewer gets a window without touching the disk.</summary>
    public MainWindowViewModel() : this(new DesignBackendFactory(), new DesignTransactionService(),
        new DesignPaneLayoutStore(), new DesignDiskSpaceProbe(),
        NullLogger<MainWindowViewModel>.Instance)
    {
    }

    /// <summary>Raised (UI thread) to show the About dialog with the given message.</summary>
    public event Action<string>? AboutRequested;

    /// <summary>Raised (UI thread) to open the Conflicts window with what was reported.</summary>
    public event Action<IReadOnlyList<TransactionConflict>>? ConflictsRequested;

    /// <summary>Raised (UI thread) to open the Repositories window.</summary>
    public event Action? RepositoriesRequested;

    /// <summary>Raised (UI thread) to open the User Software window.</summary>
    public event Action? UserSoftwareRequested;

    /// <summary>Raised (UI thread) to close the window, from File &#8594; Close.</summary>
    public event Action? CloseRequested;

    public PanesViewModel Panes { get; }
    public InventoryViewModel Inventory { get; } = new();
    public StatusViewModel Status { get; } = new();
    public DiskSpaceViewModel DiskSpace { get; }
    public LogPaneViewModel Log { get; } = new();

    public ReactiveCommand<Unit, Unit> Lookup { get; }
    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Start { get; }
    public ReactiveCommand<Unit, Unit> Stop { get; }
    public ReactiveCommand<Unit, Unit> ShowConflicts { get; }
    public ReactiveCommand<Unit, Unit> ShowAbout { get; }
    public ReactiveCommand<Unit, Unit> ShowRepositories { get; }
    public ReactiveCommand<Unit, Unit> ShowUserSoftware { get; }
    public ReactiveCommand<Unit, Unit> SelectAll { get; }
    public ReactiveCommand<Unit, Unit> DeselectAll { get; }
    public ReactiveCommand<Unit, Unit> MarkForInstall { get; }
    public ReactiveCommand<Unit, Unit> MarkForRemoval { get; }

    public string Title => Strings.SoftwareManager;

    /// <summary>What the Available Software field holds: a search term, or a path to a package file.</summary>
    public string SourceText
    {
        get => _sourceText;
        set => this.RaiseAndSetIfChanged(ref _sourceText, value);
    }

    /// <summary>The exact command the transaction is running, for the Command pane.</summary>
    public string CommandLine
    {
        get => _commandLine;
        set => this.RaiseAndSetIfChanged(ref _commandLine, value);
    }

    /// <summary>Whether the plan came back with anything for the user to resolve.</summary>
    public bool HasConflicts
    {
        get => _hasConflicts;
        set => this.RaiseAndSetIfChanged(ref _hasConflicts, value);
    }

    /// <summary>Whether a transaction is in flight, which is what Stop is for and Start is not.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isRunning, value);
            this.RaisePropertyChanged(nameof(CanStart));
        }
    }

    /// <summary>
    /// Whether Start has anything to do: something is marked, nothing is already running, and
    /// there is a way to run it as root at all. A Start button that authenticates and then finds
    /// it has nothing to do would be worse than one that is visibly not ready.
    /// </summary>
    public bool CanStart => !_isRunning && _transactions.IsAvailable && Inventory.HasMarks;

    public ManagerMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsInstallMode));
            this.RaisePropertyChanged(nameof(IsManageMode));
            this.RaisePropertyChanged(nameof(IsUpdatesMode));
            Reload();
        }
    }

    // The three mode buttons are toggles rather than radio buttons -- that is the IRIX look --
    // so each one binds to its own bool. Setting one to true moves Mode; setting one to false
    // is ignored, because a mode row with nothing lit is not a state the window has.
    public bool IsInstallMode
    {
        get => _mode == ManagerMode.Install;
        set => SetMode(ManagerMode.Install, value);
    }

    public bool IsManageMode
    {
        get => _mode == ManagerMode.Manage;
        set => SetMode(ManagerMode.Manage, value);
    }

    public bool IsUpdatesMode
    {
        get => _mode == ManagerMode.Updates;
        set => SetMode(ManagerMode.Updates, value);
    }

    /// <summary>Fills the inventory for the current mode. Called once the window is shown.</summary>
    public void Reload()
    {
        // Cleared as well as disposed. Most of the paths below answer without starting a query
        // at all and return early, and a field still holding the disposed source would make the
        // *next* Reload throw on Cancel -- so the crash landed one action after the one that
        // caused it, on whichever of Lookup, Refresh or a mode button came next.
        _query?.Cancel();
        _query?.Dispose();
        _query = null;

        if (!_system.IsSupported)
        {
            Status.Message = Strings.StatusNoBackend;
            Inventory.Replace([]);
            return;
        }

        var mode = _mode;
        var query = _sourceText.Trim();

        // A path in the field means a package file, not a search term. Lookup inspects it and
        // offers it as a single row; Start then installs it through the same machinery as
        // anything else. This is why the field takes both -- it is the IRIX original's one
        // field for "where the software is", and a local file is one of the answers.
        if (mode == ManagerMode.Install && LooksLikeAPath(query))
        {
            ShowPackageFile(query);
            return;
        }

        if (mode == ManagerMode.Install && query.Length == 0)
        {
            Status.Message = Strings.StatusNeedsQuery(_system.Backend.Id);
            Inventory.Replace([]);
            return;
        }

        Status.Message = mode switch
        {
            ManagerMode.Install => Strings.StatusSearching,
            ManagerMode.Manage => Strings.StatusReadingInstalled,
            _ => Strings.StatusCheckingUpdates,
        };

        Inventory.IsBusy = true;
        var cancellation = _query = new CancellationTokenSource();
        _ = LoadAsync(mode, query, cancellation.Token);
    }

    private async Task LoadAsync(ManagerMode mode, string query, CancellationToken cancellationToken)
    {
        try
        {
            var packages = mode switch
            {
                ManagerMode.Install => await _system.Backend.SearchAsync(query, cancellationToken)
                    .ConfigureAwait(false),
                ManagerMode.Manage => await _system.Backend.ListInstalledAsync(cancellationToken)
                    .ConfigureAwait(false),
                _ => await _system.Backend.ListUpdatesAsync(cancellationToken).ConfigureAwait(false),
            };

            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() => Show(mode, query, packages));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query. The one that replaced it owns the list now.
        }
        catch (Exception ex)
        {
            _logger.ZLogError(ex, $"The {_system.Backend.Id} backend failed to answer.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Inventory.IsBusy = false;
                Inventory.Replace([]);
                Status.Message = Strings.StatusQueryFailed(_system.Backend.Id);
                Log.Append(ex.Message);
            });
        }
    }

    /// <summary>
    /// Whether what the user typed is a path rather than a search term. Absolute, or starting
    /// with <c>~</c> or <c>./</c> — a package name never does any of those, so there is no
    /// query this steals.
    /// </summary>
    private static bool LooksLikeAPath(string text) =>
        text.StartsWith('/') || text.StartsWith("~/", StringComparison.Ordinal)
                             || text.StartsWith("./", StringComparison.Ordinal);

    /// <summary>
    /// Offers a package file from disk as a single row, ready to be marked and installed.
    ///
    /// Nothing is unpacked to find out what is inside: reading a package's metadata means
    /// running the package manager over it, and the version and size the row would gain are not
    /// worth doing that before the user has said they want it. The row carries the file name,
    /// and the transaction itself reports what it actually installed.
    /// </summary>
    private void ShowPackageFile(string text)
    {
        var path = Path.GetFullPath(text.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..])
            : text);

        Inventory.IsBusy = false;

        if (!File.Exists(path))
        {
            Inventory.Replace([]);
            Status.Message = Strings.FileNotFound(path);
            return;
        }

        // The same validation the helper will apply, run here so an unsupported file is refused
        // with an explanation instead of failing after the user has authenticated.
        if (!HelperRequest.TryParse(
                ["installfile", _system.Backend.Id, path], fileMustExist: true, out _, out var error))
        {
            Inventory.Replace([]);
            Status.Message = Strings.TransactionRefused(error);
            return;
        }

        _packageFile = path;
        Inventory.Replace([
            new PackageRowViewModel(path, version: string.Empty,
                statusText: Strings.PackageStatus(PackageStatus.New),
                sizeKilobytes: new FileInfo(path).Length / 1024,
                typeText: Strings.TypeLocalFile,
                canInstall: true, canRemove: false),
        ]);

        Status.Message = Strings.FileReady(Path.GetFileName(path));
    }

    private void Show(ManagerMode mode, string query, IReadOnlyList<PackageInfo> packages)
    {
        Inventory.IsBusy = false;
        _packageFile = null;
        Inventory.Replace(packages.Select(package => new PackageRowViewModel(
            package.Name,
            package.Version,
            Strings.PackageStatus(package.Status),
            package.InstalledSizeKilobytes,
            package.Repository,
            // In Manage mode nothing is offered for installation, even where a newer version
            // exists: that is what Updates mode is for, and a window showing both columns in
            // every mode is how a user marks the wrong one.
            package.CanInstall && mode != ManagerMode.Manage,
            package.CanRemove && mode != ManagerMode.Updates)));

        Status.Message = (packages.Count, mode) switch
        {
            (0, ManagerMode.Install) => Strings.StatusNothingFound(query),
            (0, ManagerMode.Updates) => Strings.StatusUpToDate,
            (0, _) => Strings.StatusFound(0),
            _ => Strings.StatusFound(packages.Count),
        };
    }

    private void SetMode(ManagerMode mode, bool selected)
    {
        if (selected)
            Mode = mode;
        else
            this.RaisePropertyChanged(mode == _mode ? ModePropertyName(mode) : nameof(Mode));
    }

    private static string ModePropertyName(ManagerMode mode) => mode switch
    {
        ManagerMode.Install => nameof(IsInstallMode),
        ManagerMode.Manage => nameof(IsManageMode),
        _ => nameof(IsUpdatesMode),
    };

    /// <summary>
    /// Carries out what the user has marked.
    ///
    /// Removals go first, then installations. Two transactions rather than one because no
    /// package manager here takes both in a single command, and in that order because a removal
    /// making room for an installation is the case that works, where the reverse can fail on
    /// disk space it was about to free. polkit's grace window means the user still authenticates
    /// once.
    /// </summary>
    private void RunStart()
    {
        var requests = new List<HelperRequest>();
        var backend = _system.Backend.Id;

        // A row that came from a path installs the file; everything else installs by name.
        var installVerb = _packageFile is null ? HelperVerb.Install : HelperVerb.InstallFile;

        foreach (var (verb, targets) in new[]
                 {
                     (HelperVerb.Remove, Inventory.MarkedForRemoval),
                     (installVerb, Inventory.MarkedForInstall),
                 })
        {
            if (targets.Count == 0)
                continue;

            if (HelperRequest.TryParse([verb.ToString().ToLowerInvariant(), backend, .. targets],
                    fileMustExist: verb == HelperVerb.InstallFile, out var request, out var error))
            {
                requests.Add(request);
            }
            else
            {
                // The names came from the package manager's own listing, so this should not
                // happen -- which is exactly why it is reported rather than swallowed.
                _logger.ZLogError($"Refusing to build a {verb} transaction: {error}");
                Status.Message = Strings.TransactionRefused(error);
                return;
            }
        }

        if (requests.Count == 0)
            return;

        Begin(requests);
    }

    /// <summary>
    /// Software &#8594; Refresh Package Lists: re-download what the repositories are offering.
    ///
    /// A privileged transaction of its own, because that is what refreshing the lists is on
    /// every one of the three — <c>pacman -Sy</c>, <c>apt-get update</c>, <c>zypper refresh</c>
    /// all write to a system cache. Reloading the window afterwards is what
    /// <see cref="RunTransactionsAsync"/> already does on success.
    /// </summary>
    private void RunRefresh()
    {
        if (!HelperRequest.TryParse(["refresh", _system.Backend.Id], fileMustExist: false,
                out var request, out var error))
        {
            _logger.ZLogError($"Refusing to build a refresh transaction: {error}");
            Status.Message = Strings.TransactionRefused(error);
            return;
        }

        Begin([request]);
    }

    /// <summary>Clears the panes down and starts a run of transactions.</summary>
    private void Begin(IReadOnlyList<HelperRequest> requests)
    {
        Log.Clear();
        Status.ResetProgress();
        CommandLine = string.Empty;
        _conflicts.Clear();
        HasConflicts = false;
        IsRunning = true;

        _transaction = new CancellationTokenSource();
        _ = RunTransactionsAsync(requests, _transaction.Token);
    }

    private async Task RunTransactionsAsync(IReadOnlyList<HelperRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = new TransactionResult(TransactionOutcome.Succeeded, null);

        foreach (var request in requests)
        {
            result = await _transactions.RunAsync(request, this, cancellationToken)
                .ConfigureAwait(true);

            // Stop at the first thing that did not work. Carrying on to install after a removal
            // failed would act on a system in a state the user did not ask for.
            if (result.Outcome != TransactionOutcome.Succeeded)
                break;
        }

        IsRunning = false;
        _transaction?.Dispose();
        _transaction = null;

        Status.Message = result.Outcome switch
        {
            TransactionOutcome.Succeeded => Strings.TransactionDone,
            TransactionOutcome.NotAuthorized => Strings.TransactionNotAuthorized,
            TransactionOutcome.Canceled => Strings.TransactionStopped,
            TransactionOutcome.Unavailable => result.Message ?? Strings.TransactionFailed,
            _ => result.Message ?? Strings.TransactionFailed,
        };

        if (result.Outcome == TransactionOutcome.Succeeded)
        {
            Status.Initialize = 100;
            Status.Install = 100;
            Status.PostInstall = 100;
            Inventory.ClearMarks();

            // What is installed has changed, so the list on screen is now describing the system
            // as it was before the transaction.
            Reload();
            DiskSpace.Refresh();
        }
    }

    private void RunStop()
    {
        // Cancelling kills the helper, not the package manager under it: a package manager
        // stopped halfway through writing files is how a system ends up needing a rescue disk.
        // What this actually stops is watching -- which is why the button is Stop and not Undo.
        _transaction?.Cancel();
    }

    void ITransactionObserver.OnCommand(string command) => CommandLine = command;

    void ITransactionObserver.OnLog(string line, bool isError)
    {
        Log.Append(line);

        // Conflicts are read off the running transaction rather than a dry run, because
        // `pacman -S --print` does not resolve them at all. See ConflictDetector.
        if (ConflictDetector.Read(_system.Backend.Id, line) is { } conflict)
        {
            _conflicts.Add(conflict);
            HasConflicts = true;
        }
    }

    void ITransactionObserver.OnProgress(TransactionPhase phase, double? percent)
    {
        // A phase starting means the ones before it finished, whether or not their own last
        // line was ever seen. Without that a missed line leaves a bar stuck at 40% for the rest
        // of the transaction.
        switch (phase)
        {
            case TransactionPhase.Initialize:
                Status.Initialize = percent ?? Math.Max(Status.Initialize, 10);
                break;

            case TransactionPhase.Install:
                Status.Initialize = 100;
                Status.Install = percent ?? Math.Max(Status.Install, 10);
                break;

            case TransactionPhase.PostInstall:
                Status.Initialize = 100;
                Status.Install = 100;
                Status.PostInstall = percent ?? Math.Max(Status.PostInstall, 10);
                break;
        }
    }

    private void MarkAll(bool install)
    {
        foreach (var row in Inventory.Rows)
        {
            if (install && row.CanInstall)
                row.MarkedForInstall = true;
            else if (!install && row.CanRemove)
                row.MarkedForRemoval = true;
        }
    }

    private void MarkSelected(bool install)
    {
        if (Inventory.Selected is not { } row)
            return;

        if (install && row.CanInstall)
            row.MarkedForInstall = true;
        else if (!install && row.CanRemove)
            row.MarkedForRemoval = true;
    }

    /// <summary>
    /// Keeps Start and the Disk Space pane in step with what is marked.
    ///
    /// The net change is summed from sizes the listing already carried, rather than from a
    /// dry run: those sizes came back with the packages, so the pie moves as the user ticks
    /// boxes instead of after a round trip to the package manager. It is an estimate — it
    /// counts what was asked for and not the dependencies that will come with it — and the
    /// real figure lands in the log when the transaction runs.
    /// </summary>
    private void OnMarksChanged()
    {
        this.RaisePropertyChanged(nameof(CanStart));

        var added = Inventory.Rows.Where(row => row.MarkedForInstall).Sum(row => row.SizeKilobytes);
        var removed = Inventory.Rows.Where(row => row.MarkedForRemoval).Sum(row => row.SizeKilobytes);

        DiskSpace.NetChangeKilobytes = added - removed;
        DiskSpace.OverheadKilobytes = added;
    }

    /// <summary>
    /// A command faulted. Nothing here should ever throw, so this is a bug report rather than
    /// an error path -- but it belongs in the log and the status line, not in a stack trace on
    /// the way out of the process.
    /// </summary>
    private void OnCommandFailed(Exception exception)
    {
        _logger.ZLogError(exception, $"A command failed.");
        Status.Message = Strings.UnexpectedError(exception.Message);
        IsRunning = false;
    }

    private void RaiseConflicts() => ConflictsRequested?.Invoke(_conflicts.ToList());

    private void RaiseAbout()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        AboutRequested?.Invoke(Strings.AboutMessage(version));
    }

    /// <summary>File &#8594; Close.</summary>
    public void Close() => CloseRequested?.Invoke();

    public void Dispose()
    {
        _query?.Cancel();
        _query?.Dispose();
        _query = null;
        _transaction?.Cancel();
        _transaction?.Dispose();
        _transaction = null;
        _subscriptions.Dispose();
    }

    private sealed class DesignPaneLayoutStore : IPaneLayoutStore
    {
        public PaneLayout Load() => new();

        public void Save(PaneLayout layout)
        {
        }
    }

    private sealed class DesignDiskSpaceProbe : IDiskSpaceProbe
    {
        public IReadOnlyList<DiskUsage> Probe() => [new DiskUsage("/", 98_158_900, 4_799_640)];
    }

    private sealed class DesignBackendFactory : IPackageBackendFactory
    {
        public PackageSystem Resolve() =>
            new(new NullBackend(), new DistributionInfo("arch", "Arch Linux", ["arch"]));
    }

    private sealed class DesignTransactionService : ITransactionService
    {
        public bool IsAvailable => false;

        public Task<TransactionResult> RunAsync(HelperRequest request, ITransactionObserver observer,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransactionResult(TransactionOutcome.Unavailable, null));
    }
}
