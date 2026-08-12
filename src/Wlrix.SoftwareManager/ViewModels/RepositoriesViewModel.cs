using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Wlrix.Packages;
using Wlrix.Packages.Models;
using Wlrix.Packages.Privileged;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;
using ZLogger;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>One row of the Repositories window.</summary>
public sealed class RepositoryRowViewModel(RepositoryInfo repository) : ViewModelBase
{
    public string Id => repository.Id;
    public string Name => repository.Name;
    public string Url => repository.Url;
    public bool IsEnabled => repository.IsEnabled;

    /// <summary>What the Status column shows.</summary>
    public string StateText => repository.IsEnabled ? Strings.RepositoryEnabled : Strings.RepositoryDisabled;
}

/// <summary>
/// The Repositories window: what software sources are configured, and — where the package
/// manager has a command for it — adding, removing and switching them.
/// </summary>
public sealed class RepositoriesViewModel : ViewModelBase, ITransactionObserver
{
    private readonly PackageSystem _system;
    private readonly ITransactionService _transactions;
    private readonly ILogger<RepositoriesViewModel> _logger;

    private RepositoryRowViewModel? _selected;
    private string _newAlias = string.Empty;
    private string _newUrl = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;

    public RepositoriesViewModel(IPackageBackendFactory backendFactory,
        ITransactionService transactions, ILogger<RepositoriesViewModel> logger)
    {
        _system = backendFactory.Resolve();
        _transactions = transactions;
        _logger = logger;

        var canModify = _system.Backend.Capabilities.HasFlag(BackendCapabilities.ModifyRepositories)
                        && _transactions.IsAvailable;

        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy && canModify);
        var hasSelection = this.WhenAnyValue(x => x.Selected, x => x.IsBusy,
            (selected, busy) => selected is not null && !busy && canModify);

        Add = ReactiveCommand.Create(() => Run(HelperVerb.RepoAdd, [_newAlias.Trim(), _newUrl.Trim()]), idle);
        Remove = ReactiveCommand.Create(() => Run(HelperVerb.RepoRemove, [Selected!.Id]), hasSelection);
        Enable = ReactiveCommand.Create(() => Run(HelperVerb.RepoEnable, [Selected!.Id]), hasSelection);
        Disable = ReactiveCommand.Create(() => Run(HelperVerb.RepoDisable, [Selected!.Id]), hasSelection);

        // pacman is the case this exists for. It can be read but has no command for the writes,
        // and the alternative -- editing a hand-owned pacman.conf on the user's behalf -- is the
        // failure wlrix-settings-daemon was built to stop. The window says so rather than
        // offering four buttons that would all report the same refusal.
        CanModify = canModify;
        if (!_system.Backend.Capabilities.HasFlag(BackendCapabilities.ModifyRepositories))
            _status = Strings.RepositoriesReadOnly(_system.Backend.Id);
    }

    /// <summary>Design-time constructor.</summary>
    public RepositoriesViewModel() : this(new DesignBackendFactory(), new DesignTransactionService(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RepositoriesViewModel>.Instance)
    {
    }

    public ObservableCollection<RepositoryRowViewModel> Repositories { get; } = [];

    public ReactiveCommand<Unit, Unit> Add { get; }
    public ReactiveCommand<Unit, Unit> Remove { get; }
    public ReactiveCommand<Unit, Unit> Enable { get; }
    public ReactiveCommand<Unit, Unit> Disable { get; }

    /// <summary>Whether this package manager can be asked to change its sources at all.</summary>
    public bool CanModify { get; }

    public RepositoryRowViewModel? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    /// <summary>The alias for a repository being added.</summary>
    public string NewAlias
    {
        get => _newAlias;
        set => this.RaiseAndSetIfChanged(ref _newAlias, value);
    }

    /// <summary>The URL for a repository being added.</summary>
    public string NewUrl
    {
        get => _newUrl;
        set => this.RaiseAndSetIfChanged(ref _newUrl, value);
    }

    /// <summary>What happened, when it is worth saying.</summary>
    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>Re-reads the configured sources.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var repositories = await _system.Backend.ListRepositoriesAsync().ConfigureAwait(true);

            Repositories.Clear();
            foreach (var repository in repositories)
                Repositories.Add(new RepositoryRowViewModel(repository));
        }
        catch (Exception ex)
        {
            _logger.ZLogError(ex, $"Could not list the repositories.");
            Status = Strings.StatusQueryFailed(_system.Backend.Id);
        }
    }

    private void Run(HelperVerb verb, IReadOnlyList<string> targets)
    {
        if (!HelperRequest.TryParse([verb.ToString().ToLowerInvariant(), _system.Backend.Id, .. targets],
                fileMustExist: false, out var request, out var error))
        {
            Status = Strings.TransactionRefused(error);
            return;
        }

        IsBusy = true;
        Status = string.Empty;
        _ = RunAsync(request);
    }

    private async Task RunAsync(HelperRequest request)
    {
        var result = await _transactions.RunAsync(request, this).ConfigureAwait(true);

        IsBusy = false;
        Status = result.Outcome switch
        {
            TransactionOutcome.Succeeded => Strings.TransactionDone,
            TransactionOutcome.NotAuthorized => Strings.TransactionNotAuthorized,
            TransactionOutcome.Canceled => Strings.TransactionStopped,
            _ => result.Message ?? Strings.TransactionFailed,
        };

        if (result.Outcome == TransactionOutcome.Succeeded)
        {
            NewAlias = string.Empty;
            NewUrl = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(LoadAsync);
        }
    }

    // The window has no log pane of its own; what the package manager said goes to the log file
    // and, if it failed, its last words end up in the status line through the outcome message.
    void ITransactionObserver.OnCommand(string command) => _logger.ZLogInformation($"Running: {command}");

    void ITransactionObserver.OnLog(string line, bool isError)
    {
        if (isError)
            _logger.ZLogWarning($"{line}");
    }

    void ITransactionObserver.OnProgress(Packages.Backends.Parsing.TransactionPhase phase, double? percent)
    {
    }

    private sealed class DesignBackendFactory : IPackageBackendFactory
    {
        public PackageSystem Resolve() => new(new Packages.Backends.NullBackend(),
            new DistributionInfo("arch", "Arch Linux", ["arch"]));
    }

    private sealed class DesignTransactionService : ITransactionService
    {
        public bool IsAvailable => false;

        public Task<TransactionResult> RunAsync(HelperRequest request, ITransactionObserver observer,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransactionResult(TransactionOutcome.Unavailable, null));
    }
}
