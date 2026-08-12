using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Packages;
using Wlrix.Packages.Backends;
using Wlrix.Packages.Models;
using Wlrix.Packages.Privileged;
using Wlrix.SoftwareManager.Services;
using Wlrix.SoftwareManager.ViewModels;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>
/// The window's state machine. Everything here stays on the paths that answer without starting
/// a package manager, so none of it needs a display or a dispatcher.
/// </summary>
public class MainWindowViewModelTests
{
    private static MainWindowViewModel Create() => new(
        new FakeBackendFactory(),
        new FakeTransactionService(),
        new FakePaneLayoutStore(),
        new FakeDiskSpaceProbe(),
        NullLogger<MainWindowViewModel>.Instance);

    [Fact]
    public void Reload_SurvivesBeingCalledAgainAfterAModeThatAnsweredWithoutAQuery()
    {
        // The reported crash. Manage mode starts a listing and so creates a token source;
        // switching to Install with an empty field answers early and used to leave the field
        // holding that source after disposing it. The next Reload -- Refresh, Lookup, or another
        // mode button -- then threw ObjectDisposedException from Cancel.
        using var viewModel = Create();

        viewModel.Mode = ManagerMode.Manage;
        viewModel.Mode = ManagerMode.Install;

        viewModel.Reload();
        viewModel.Reload();
    }

    [Fact]
    public void Reload_SurvivesAnyOrderOfModeSwitching()
    {
        using var viewModel = Create();

        foreach (var mode in new[]
                 {
                     ManagerMode.Install, ManagerMode.Manage, ManagerMode.Updates,
                     ManagerMode.Install, ManagerMode.Updates, ManagerMode.Manage,
                     ManagerMode.Install,
                 })
        {
            viewModel.Mode = mode;
            viewModel.Reload();
        }
    }

    [Fact]
    public void Reload_SurvivesTheFileInspectionPathBeingTakenTwice()
    {
        // The third early return: a path in the Available Software field. Same shape, same
        // stale token source if it were left behind.
        using var viewModel = Create();

        viewModel.Mode = ManagerMode.Manage;
        viewModel.SourceText = "/tmp/there-is-no-such-package.deb";
        viewModel.Mode = ManagerMode.Install;

        viewModel.Reload();
        viewModel.Reload();
    }

    [Fact]
    public void Reload_InInstallModeWithNothingTypedAsksForAQueryRatherThanSearching()
    {
        using var viewModel = Create();

        viewModel.Reload();

        Assert.Empty(viewModel.Inventory.Rows);
        Assert.Contains("Lookup", viewModel.Status.Message);
        Assert.False(viewModel.Inventory.IsBusy, "an empty query should not have started a search");
    }

    [Fact]
    public void Refresh_IsDisabledWhenNothingCanBeRunAsRoot()
    {
        // Refreshing the package lists is a privileged transaction. Without a way to run one the
        // menu item does nothing, so it says so by being unavailable.
        using var viewModel = Create();

        Assert.False(((System.Windows.Input.ICommand)viewModel.Refresh).CanExecute(null));
    }

    [Fact]
    public void Start_IsDisabledUntilSomethingIsMarked()
    {
        using var viewModel = Create();

        Assert.False(viewModel.CanStart);
    }

    private sealed class FakeBackendFactory : IPackageBackendFactory
    {
        public PackageSystem Resolve() =>
            new(new FakeBackend(), new DistributionInfo("arch", "Arch Linux", ["arch"]));
    }

    /// <summary>
    /// A backend that is supported but never answers.
    ///
    /// Both halves matter. Supported, so <see cref="MainWindowViewModel.Reload"/> gets past its
    /// first early return and actually creates the token source these tests are about — a
    /// <c>NullBackend</c> would turn every one of them green without exercising anything. Never
    /// answering, so the listing stays parked at the await and never reaches the dispatcher,
    /// which no test here has a UI thread for.
    /// </summary>
    private sealed class FakeBackend : IPackageBackend
    {
        public string Id => "fake";

        public BackendCapabilities Capabilities => BackendCapabilities.InstallFromRepository;

        public Task<IReadOnlyList<PackageInfo>> SearchAsync(string query,
            CancellationToken cancellationToken = default) => Never<PackageInfo>(cancellationToken);

        public Task<IReadOnlyList<PackageInfo>> ListInstalledAsync(
            CancellationToken cancellationToken = default) => Never<PackageInfo>(cancellationToken);

        public Task<IReadOnlyList<PackageInfo>> ListUpdatesAsync(
            CancellationToken cancellationToken = default) => Never<PackageInfo>(cancellationToken);

        public Task<PackageDetails?> DescribeAsync(string name,
            CancellationToken cancellationToken = default) => Task.FromResult<PackageDetails?>(null);

        public Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync(
            CancellationToken cancellationToken = default) => Never<RepositoryInfo>(cancellationToken);

        private static async Task<IReadOnlyList<T>> Never<T>(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return [];
        }
    }

    private sealed class FakeTransactionService : ITransactionService
    {
        public bool IsAvailable => false;

        public Task<TransactionResult> RunAsync(HelperRequest request, ITransactionObserver observer,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransactionResult(TransactionOutcome.Unavailable, null));
    }

    private sealed class FakePaneLayoutStore : IPaneLayoutStore
    {
        public PaneLayout Load() => new();

        public void Save(PaneLayout layout)
        {
        }
    }

    private sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
    {
        public IReadOnlyList<DiskUsage> Probe() => [new DiskUsage("/", 1000, 100)];
    }
}
