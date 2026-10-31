using Wlrix.Packages.Backends.Parsing;
using Xunit;

namespace Wlrix.Packages.Tests;

public class TransactionProgressTests
{
    [Theory]
    [InlineData("pacman", ":: Synchronizing package databases...", TransactionPhase.Initialize)]
    [InlineData("pacman", "resolving dependencies...", TransactionPhase.Initialize)]
    [InlineData("pacman", "looking for conflicting packages...", TransactionPhase.Initialize)]
    [InlineData("pacman", ":: Retrieving packages...", TransactionPhase.Install)]
    [InlineData("pacman", "(1/2) installing cowsay", TransactionPhase.Install)]
    [InlineData("pacman", "(2/2) upgrading zsh", TransactionPhase.Install)]
    [InlineData("pacman", ":: Running post-transaction hooks...", TransactionPhase.PostInstall)]
    [InlineData("apt", "Reading package lists...", TransactionPhase.Initialize)]
    [InlineData("apt", "Get:1 http://deb.debian.org/debian bookworm/main amd64 cowsay all", TransactionPhase.Install)]
    [InlineData("apt", "Unpacking cowsay (3.03+dfsg2-8) ...", TransactionPhase.Install)]
    [InlineData("apt", "Setting up cowsay (3.03+dfsg2-8) ...", TransactionPhase.PostInstall)]
    [InlineData("apt", "Processing triggers for man-db (2.11.2-2) ...", TransactionPhase.PostInstall)]
    [InlineData("zypper", "Loading repository data...", TransactionPhase.Initialize)]
    [InlineData("zypper", "Resolving package dependencies...", TransactionPhase.Initialize)]
    [InlineData("zypper", "Retrieving package cowsay-3.04-1.5.noarch (1/2)", TransactionPhase.Install)]
    [InlineData("zypper", "Installing: cowsay-3.04-1.5 (2/2)", TransactionPhase.Install)]
    [InlineData("zypper", "Checking for file conflicts:", TransactionPhase.PostInstall)]
    public void Read_PlacesALineInItsPhase(string backend, string line, TransactionPhase expected)
    {
        var update = TransactionProgress.Read(backend, line);

        Assert.NotNull(update);
        Assert.Equal(expected, update.Phase);
    }

    [Theory]
    [InlineData("pacman", "(1/4) installing cowsay", 25)]
    [InlineData("pacman", "(4/4) installing zsh", 100)]
    [InlineData("zypper", "Installing: cowsay-3.04-1.5 (3/6)", 50)]
    public void Read_TurnsTheCountedStepIntoAPercentage(string backend, string line, double expected)
    {
        var update = TransactionProgress.Read(backend, line);

        Assert.NotNull(update);
        Assert.Equal(expected, update.Percent);
    }

    [Fact]
    public void Read_LeavesThePercentageUnknownWhenTheLineDidNotCount() =>
        Assert.Null(TransactionProgress.Read("pacman", "resolving dependencies...")!.Percent);

    [Fact]
    public void Read_ReadsAHookLineAsPostInstallEvenThoughItAlsoCarriesACounter()
    {
        // pacman's hooks count "( 1/12) Reloading system manager configuration". That is the
        // same (n/m) shape as an install step, and it comes after the install, so a line
        // matching both belongs to the later phase.
        var update = TransactionProgress.Read("pacman", "( 1/12) Arming ConditionNeedsUpdate hook");

        Assert.NotNull(update);
        Assert.Equal(TransactionPhase.PostInstall, update.Phase);
    }

    [Fact]
    public void Read_SaysNothingAboutALineThatSaysNothing() =>
        Assert.Null(TransactionProgress.Read("pacman", "warning: cowsay is up to date -- skipping"));

    [Fact]
    public void Read_SaysNothingForABackendItDoesNotKnow() =>
        Assert.Null(TransactionProgress.Read("dnf", "Installing: cowsay (1/2)"));

    [Fact]
    public void Read_SurvivesADividedByZeroCounter() =>
        // Not a line any of them prints, but this is inference over free text and it must not
        // be the thing that takes down a transaction the user is watching.
        Assert.Null(TransactionProgress.Read("pacman", "(0/0) installing nothing")!.Percent);

    [Fact]
    public void Meter_FollowsARecordedPacmanDownloadAndInstall()
    {
        // pacman 7 under LC_ALL=C with no terminal, as the helper runs it, fetching one package
        // from a local server after refreshing its database.
        var meter = new TransactionMeter("pacman");
        var seen = new List<double>();
        foreach (var line in Fixture.Read("pacman-download.txt").Split('\n'))
        {
            meter.Read(line);
            seen.Add(meter.Percent);
        }

        // A database download is not a package: the bar has not moved past the plan because of it.
        Assert.Equal(0, seen[1]);
        Assert.Equal(40, seen[Array.IndexOf(Fixture.Read("pacman-download.txt").Split('\n'), " wlrix-fixture-1-1-any downloading...")]);
        Assert.Equal(90, seen[^1]);
        Assert.Equal(seen.Order(), seen);
    }

    [Fact]
    public void Meter_CountsPacmanOperationsAgainstThePlan()
    {
        var meter = new TransactionMeter("pacman");
        meter.Read("Packages (2) first-1-1 second-1-1");
        meter.Read("upgrading first...");

        Assert.Equal(65, meter.Percent);
    }

    [Fact]
    public void Meter_CountsPacmanHooksOnceTheyStart()
    {
        var meter = new TransactionMeter("pacman");
        meter.Read("(1/2) Arming ConditionNeedsUpdate...");
        Assert.Equal(0, meter.Percent);

        meter.Read(":: Running post-transaction hooks...");
        Assert.Equal(90, meter.Percent);

        meter.Read("(1/2) Arming ConditionNeedsUpdate...");
        Assert.Equal(95, meter.Percent);
    }

    [Fact]
    public void Meter_NeverGoesBackward()
    {
        var meter = new TransactionMeter("pacman");
        meter.Read(":: Retrieving packages...");
        var before = meter.Percent;
        meter.Read("checking keyring...");

        Assert.Equal(before, meter.Percent);
    }

    [Fact]
    public void Meter_UsesZyppersOwnCount()
    {
        var meter = new TransactionMeter("zypper");
        meter.Read("Installing: foo-1.0 (5/10)");

        Assert.Equal(50, meter.Percent);
    }
}
