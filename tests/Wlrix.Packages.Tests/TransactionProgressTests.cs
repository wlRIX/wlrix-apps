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
}
