using Wlrix.Packages.Backends.Parsing;
using Xunit;

namespace Wlrix.Packages.Tests;

public class ConflictDetectorTests
{
    [Fact]
    public void Read_PacmanNamesBothSidesOfAConflict()
    {
        var conflict = ConflictDetector.Read("pacman",
            ":: cowsay and cowsay-git are in conflict. Remove cowsay-git? [y/N]");

        Assert.NotNull(conflict);
        Assert.Equal("cowsay", conflict.Package);
        Assert.Contains("cowsay-git", conflict.Description);
    }

    [Fact]
    public void Read_PacmanReportsAFailedPrepareWithItsReason()
    {
        var conflict = ConflictDetector.Read("pacman",
            "error: failed to prepare transaction (could not satisfy dependencies)");

        Assert.NotNull(conflict);
        Assert.Equal("could not satisfy dependencies", conflict.Description);
    }

    [Fact]
    public void Read_AptReportsAnUnmetDependencyAgainstThePackageThatHasIt()
    {
        var conflict = ConflictDetector.Read("apt",
            " cowsay : Conflicts: cowsay-git but 1.0-1 is to be installed");

        Assert.NotNull(conflict);
        Assert.Equal("cowsay", conflict.Package);
        Assert.Contains("Conflicts", conflict.Description);
    }

    [Fact]
    public void Read_ZypperReportsAProblemLine()
    {
        var conflict = ConflictDetector.Read("zypper",
            "Problem: cowsay-3.04-1.5.noarch conflicts with cowsay-git provided by cowsay-git-1.0");

        Assert.NotNull(conflict);
        Assert.Contains("conflicts with", conflict.Description);
    }

    [Theory]
    [InlineData("pacman", "(1/2) installing cowsay")]
    [InlineData("pacman", "warning: cowsay is up to date -- skipping")]
    [InlineData("apt", "Setting up cowsay (3.03+dfsg2-8) ...")]
    [InlineData("zypper", "Installing: cowsay-3.04-1.5 (1/1)")]
    public void Read_SaysNothingAboutAnOrdinaryLine(string backend, string line) =>
        Assert.Null(ConflictDetector.Read(backend, line));

    [Fact]
    public void Read_SaysNothingForABackendItDoesNotKnow() =>
        Assert.Null(ConflictDetector.Read("dnf", "Problem: something conflicts with something"));
}
