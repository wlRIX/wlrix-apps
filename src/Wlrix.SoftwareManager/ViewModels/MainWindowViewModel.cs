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
using Wlrix.Packages.Processes;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;
using ZLogger;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>Why a transaction failed, for the dialog that says so.</summary>
/// <param name="Message">What went wrong, in a sentence.</param>
/// <param name="Errors">The last things the package manager said on stderr.</param>
/// <param name="Conflicts">What it refused to do, for the Conflicts window.</param>
/// <param name="StaleLists">Whether a failed download suggests the package lists are out of date.</param>
public sealed record TransactionFailure(
    string Message,
    IReadOnlyList<string> Errors,
    IReadOnlyList<TransactionConflict> Conflicts,
    bool StaleLists);

/// <summary>
/// The window: the mode buttons, the Available Software field, and each pane's own view model.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, ITransactionObserver, IDisposable
{
    private readonly PackageSystem _system;
    private readonly ITransactionService _transactions;
    private readonly IRestartPendingStore _restartPending;
    private readonly ILogger<MainWindowViewModel> _logger;

    private const int MaxRecentErrors = 6;

    // One listing at a time. Switching mode or pressing Lookup while a query is in flight
    // abandons it: a slow `pacman -Qi` over two thousand packages must not land in the list
    // after the user has moved on and overwrite what they asked for next.
    private CancellationTokenSource? _query;

    // Separate from the query token: stopping a transaction and abandoning a listing are
    // different things, and Stop must not cancel a refresh that happens to be in flight.
    private CancellationTokenSource? _transaction;

    // What the package manager objected to during the transaction, for the Conflicts window.
    private readonly List<TransactionConflict> _conflicts = [];

    // The last things the package manager complained about, for the failure dialog.
    private readonly Queue<string> _recentErrors = new();

    // The commands' error subscriptions, held for the life of the view model.
    private readonly CompositeDisposable _subscriptions = [];

    // Whether the transaction's output said a download failed, which suggests stale package lists.
    private bool _sawStaleListsHint;

    private ManagerMode _mode = ManagerMode.Install;
    private string _sourceText = string.Empty;
    private string _commandLine = string.Empty;
    private string _markSummary = string.Empty;
    private bool _hasConflicts;
    private TransactionMeter? _meter;
    private int _requestIndex;
    private int _requestCount = 1;
    private bool _isRunning;
    private bool _canStop;

    public MainWindowViewModel(IPackageBackendFactory backendFactory,
        ITransactionService transactions, IPaneLayoutStore paneLayoutStore,
        IDiskSpaceProbe diskSpaceProbe, IRestartPendingStore restartPending,
        ILogger<MainWindowViewModel> logger)
    {
        _system = backendFactory.Resolve();
        _transactions = transactions;
        _restartPending = restartPending;
        _logger = logger;
        Panes = new PanesViewModel(paneLayoutStore);
        DiskSpace = new DiskSpaceViewModel(diskSpaceProbe);

        // The marks live on the rows, so nothing else would tell Start that it has work.
        Inventory.MarksChanged += OnMarksChanged;

        var idle = this.WhenAnyValue(x => x.IsRunning, running => !running);

        Lookup = ReactiveCommand.Create(() => Reload(), idle);

        // Refresh re-downloads the package lists, which is a privileged transaction -- not a
        // second Lookup. It used to be wired to Reload, which meant the menu item said
        // "Refresh Package Lists" and re-ran the current search instead. pacman is the exception:
        // it checks for updates without root, so all it needs is the Updates list.
        var refreshesUnprivileged = _system.Backend.Capabilities.HasFlag(BackendCapabilities.UnprivilegedRefresh);
        Refresh = ReactiveCommand.Create(RunRefresh, this.WhenAnyValue(x => x.IsRunning,
            running => !running && (refreshesUnprivileged || _transactions.IsAvailable)));
        Start = ReactiveCommand.Create(RunStart, this.WhenAnyValue(x => x.CanStart));
        Stop = ReactiveCommand.Create(RunStop, this.WhenAnyValue(x => x.CanStop));
        ShowConflicts = ReactiveCommand.Create(RaiseConflicts, this.WhenAnyValue(x => x.HasConflicts));
        ProductInformation = ReactiveCommand.Create(() => AboutRequested?.Invoke());
        ShowRepositories = ReactiveCommand.Create(() => RepositoriesRequested?.Invoke(),
            Observable.Return(_system.Backend.Capabilities.HasFlag(BackendCapabilities.ListRepositories)));

        // Always available: managing AppImages needs no package manager and no root, so this
        // works on a system where everything else in the window is grayed out.
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
                     Lookup, Refresh, Start, Stop, ShowConflicts, ProductInformation, ShowRepositories,
                     ShowUserSoftware, SelectAll, DeselectAll, MarkForInstall, MarkForRemoval,
                 })
            _subscriptions.Add(command.ThrownExceptions.Subscribe(OnCommandFailed));

        Status.Message = _system.IsSupported
            ? Strings.StatusIdle(_system.Distribution.Name, _system.Backend.Id)
            : Strings.StatusNoBackend;

        if (_restartPending.IsPending())
            Status.RestartNotice = Strings.RestartPending;
    }

    /// <summary>Design-time constructor: the previewer gets a window without touching the disk.</summary>
    public MainWindowViewModel() : this(new DesignBackendFactory(), new DesignTransactionService(),
        new DesignPaneLayoutStore(), new DesignDiskSpaceProbe(), new DesignRestartPendingStore(),
        NullLogger<MainWindowViewModel>.Instance)
    {
    }

    /// <summary>Raised (UI thread) to show Help &#8594; Product Information.</summary>
    public event Action? AboutRequested;

    /// <summary>Raised (UI thread) to open the Conflicts window with what was reported.</summary>
    public event Action<IReadOnlyList<TransactionConflict>>? ConflictsRequested;

    /// <summary>Raised (UI thread) to open the Repositories window.</summary>
    public event Action? RepositoriesRequested;

    /// <summary>Raised (UI thread) to open the User Software window.</summary>
    public event Action? UserSoftwareRequested;

    /// <summary>
    /// Raised (UI thread) after installations and removals succeed, saying whether the system
    /// needs a restart. Refreshing the package lists does not raise it.
    /// </summary>
    public event Action<bool>? TransactionCompleted;

    /// <summary>Raised (UI thread) when a transaction fails, with what is known about why.</summary>
    public event Action<TransactionFailure>? TransactionFailed;

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
    public ReactiveCommand<Unit, Unit> ProductInformation { get; }
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

    /// <summary>What Start will act on, shown beside it because not all of it may be in the list.</summary>
    public string MarkSummary
    {
        get => _markSummary;
        private set => this.RaiseAndSetIfChanged(ref _markSummary, value);
    }

    /// <summary>Whether the plan came back with anything for the user to resolve.</summary>
    public bool HasConflicts
    {
        get => _hasConflicts;
        set => this.RaiseAndSetIfChanged(ref _hasConflicts, value);
    }

    /// <summary>
    /// Whether Stop can do anything right now: the package manager can be interrupted safely, it
    /// has started, and it has not begun changing files.
    /// </summary>
    public bool CanStop
    {
        get => _canStop;
        private set => this.RaiseAndSetIfChanged(ref _canStop, value);
    }

    /// <summary>Whether a transaction is in flight, which is what Start is not for.</summary>
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
    /// it has nothing to do would be worse than one that is visibly not ready. In an all-or-nothing
    /// Updates list the work is the list itself.
    /// </summary>
    public bool CanStart => !_isRunning && _transactions.IsAvailable && !Inventory.IsBusy
                            && (UpgradesEverything ? Inventory.Rows.Count > 0 : Inventory.HasMarks);

    /// <summary>
    /// Whether this is an Updates list that can only be taken whole. pacman does not support
    /// partial upgrades, so Start upgrades everything and the rows cannot be unticked.
    /// </summary>
    private bool UpgradesEverything => _mode == ManagerMode.Updates
                                       && !_system.Backend.Capabilities.HasFlag(BackendCapabilities.SelectiveUpgrade);

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
            this.RaisePropertyChanged(nameof(CanStart));
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
        this.RaisePropertyChanged(nameof(CanStart));
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

        Inventory.Replace([
            new PackageRowViewModel(path, version: string.Empty,
                statusText: Strings.PackageStatus(PackageStatus.New),
                sizeKilobytes: new FileInfo(path).Length / 1024,
                typeText: Strings.TypeLocalFile,
                canInstall: true, canRemove: false, isFile: true),
        ]);

        Status.Message = Strings.FileReady(Path.GetFileName(path));
    }

    private void Show(ManagerMode mode, string query, IReadOnlyList<PackageInfo> packages)
    {
        Inventory.IsBusy = false;
        var locked = UpgradesEverything;
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
            package.CanRemove && mode != ManagerMode.Updates,
            isLocked: locked)));

        Status.ListReady();
        Status.Message = (packages.Count, mode) switch
        {
            (0, ManagerMode.Install) => Strings.StatusNothingFound(query),
            (0, ManagerMode.Updates) => Strings.StatusUpToDate,
            (0, _) => Strings.StatusFound(0),
            _ => Strings.StatusFound(packages.Count),
        };

        if (mode == ManagerMode.Updates && _system.Backend.UpdatesMayBeStale)
            Status.Message = $"{Status.Message} {Strings.UpdatesMayBeStale}";
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
    /// once. An all-or-nothing Updates list is one upgrade instead, and leaves marks made in the
    /// other modes for later.
    /// </summary>
    private void RunStart()
    {
        var requests = new List<HelperRequest>();
        var backend = _system.Backend.Id;

        foreach (var (verb, targets) in UpgradesEverything
                     ? [(HelperVerb.Upgrade, (IReadOnlyList<string>)[])]
                     : new[]
                     {
                         (HelperVerb.Remove, Inventory.MarkedForRemoval),
                         (HelperVerb.Install, Inventory.MarkedForInstall),
                         (HelperVerb.InstallFile, Inventory.MarkedFiles),
                     })
        {
            if (targets.Count == 0 && verb != HelperVerb.Upgrade)
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
    /// A privileged transaction of its own on apt and zypper, because <c>apt-get update</c> and
    /// <c>zypper refresh</c> write to a system cache. Reloading the window afterwards is what
    /// <see cref="RunTransactionsAsync"/> already does on success. pacman never refreshes its real
    /// lists on their own, since that makes the next install a partial upgrade, so there this
    /// only goes and checks for updates.
    /// </summary>
    private void RunRefresh()
    {
        if (_system.Backend.Capabilities.HasFlag(BackendCapabilities.UnprivilegedRefresh))
        {
            if (_mode == ManagerMode.Updates)
                Reload();
            else
                Mode = ManagerMode.Updates;

            return;
        }

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
        Status.Begin();
        CommandLine = string.Empty;
        _conflicts.Clear();
        _recentErrors.Clear();
        _sawStaleListsHint = false;
        HasConflicts = false;
        IsRunning = true;

        _transaction = new CancellationTokenSource();
        _ = RunTransactionsAsync(requests, _transaction.Token);
    }

    private async Task RunTransactionsAsync(IReadOnlyList<HelperRequest> requests,
        CancellationToken cancellationToken)
    {
        var result = new TransactionResult(TransactionOutcome.Succeeded, null);
        var operations = new List<PackageOperation>();
        var restartRequested = false;

        for (_requestIndex = 0; _requestIndex < requests.Count; _requestIndex++)
        {
            var request = requests[_requestIndex];
            _requestCount = requests.Count;
            _meter = new TransactionMeter(_system.Backend.Id);

            result = await _transactions.RunAsync(request, this, cancellationToken)
                .ConfigureAwait(true);

            operations.AddRange(result.Operations ?? []);
            restartRequested |= result.RestartRequested;

            // Stop at the first thing that did not work. Carrying on to install after a removal
            // failed would act on a system in a state the user did not ask for.
            if (result.Outcome != TransactionOutcome.Succeeded || cancellationToken.IsCancellationRequested)
                break;
        }

        IsRunning = false;
        CanStop = false;
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

        var isRefresh = requests is [{ Verb: HelperVerb.Refresh }];

        // Asked whatever the outcome: a transaction that failed partway may have already replaced
        // the kernel.
        var needsRestart = !isRefresh && await NeedsRestartAsync(operations, restartRequested).ConfigureAwait(true);

        if (result.Outcome == TransactionOutcome.Succeeded)
        {
            Status.Succeed();

            // Marks made in another mode are still waiting after an upgrade, which did not act on
            // them, and a refresh did not either.
            if (!isRefresh && requests is not [{ Verb: HelperVerb.Upgrade }])
                Inventory.ClearMarks();

            // What is installed has changed, so the list on screen is now describing the system
            // as it was before the transaction.
            Reload();
            DiskSpace.Refresh();

            if (!isRefresh)
                TransactionCompleted?.Invoke(needsRestart);
        }
        else
        {
            Status.Abandon();

            if (result.Outcome is TransactionOutcome.Failed or TransactionOutcome.Unavailable)
            {
                TransactionFailed?.Invoke(new TransactionFailure(Status.Message, _recentErrors.ToList(),
                    _conflicts.ToList(), _sawStaleListsHint));
            }
        }
    }

    /// <summary>
    /// Whether a restart is owed, by this transaction or an earlier one, and keeps the pending
    /// flag and the status line up to date. A check that fails is not worth a dialog of its own:
    /// it is logged and the answer is what is already known.
    /// </summary>
    private async Task<bool> NeedsRestartAsync(IReadOnlyList<PackageOperation> operations, bool requested)
    {
        var needed = requested;
        try
        {
            needed |= await _system.Backend.NeedsRestartAsync(operations).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is PackageProcessException or IOException)
        {
            _logger.ZLogWarning(ex, $"Could not check whether a restart is needed.");
        }

        if (needed)
            _restartPending.MarkPending();
        else
            needed = _restartPending.IsPending();

        Status.RestartNotice = needed ? Strings.RestartPending : string.Empty;
        return needed;
    }

    /// <summary>
    /// Checks for a restart when the window opens, which catches what was done outside this app,
    /// such as a kernel upgraded from a terminal.
    /// </summary>
    public Task CheckRestartOnOpenAsync() =>
        _system.IsSupported ? NeedsRestartAsync([], requested: false) : Task.CompletedTask;

    private void RunStop()
    {
        // A request, not a kill: the helper interrupts the package manager, and the transaction
        // goes on being watched until it really ends. Its result sets the final status.
        CanStop = false;
        Status.Message = Strings.TransactionStopping;
        _transaction?.Cancel();
    }

    void ITransactionObserver.OnCommand(string command)
    {
        CommandLine = command;
        CanStop = _system.Backend.Capabilities.HasFlag(BackendCapabilities.SafeInterrupt)
                  && _transaction is { IsCancellationRequested: false };
    }

    void ITransactionObserver.OnLog(string line, bool isError)
    {
        Log.Append(line);

        // The bar covers the whole run, and each transaction in it gets an equal share.
        _meter?.Read(line);
        if (_meter is not null)
            Status.Overall = (_requestIndex + _meter.Percent / 100) / _requestCount * 100;

        // The first package operation is the point of no return: from here, interrupting would
        // leave some packages replaced and others not.
        if (PackageOperations.Read(_system.Backend.Id, line) is not null)
            CanStop = false;

        if (isError)
        {
            _recentErrors.Enqueue(line);
            if (_recentErrors.Count > MaxRecentErrors)
                _recentErrors.Dequeue();
        }

        _sawStaleListsHint |= ConflictDetector.SuggestsStalePackageLists(_system.Backend.Id, line);

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
        // A removal and an installation in one run still go through the lights once: Post Install
        // belongs to the last transaction, while more packages are still to come before that.
        Status.EnterPhase(phase == TransactionPhase.PostInstall && _requestIndex < _requestCount - 1
            ? TransactionPhase.Install
            : phase);
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

        var added = Inventory.MarkedInstallKilobytes;
        var removed = Inventory.MarkedRemovalKilobytes;

        MarkSummary = UpgradesEverything
            ? Inventory.Rows.Count > 0 ? Strings.UpgradesEverything : string.Empty
            : Inventory.HasMarks ? Strings.MarkedCount(Inventory.MarkCount) : string.Empty;

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

    /// <summary>File &#8594; Close.</summary>
    public void Close() => CloseRequested?.Invoke();

    public void Dispose()
    {
        _query?.Cancel();
        _query?.Dispose();
        _query = null;

        // A running transaction is left to finish: closing the window is not asking it to stop.
        _subscriptions.Dispose();
    }

    private sealed class DesignRestartPendingStore : IRestartPendingStore
    {
        public bool IsPending() => false;

        public void MarkPending()
        {
        }
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
