using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Xunit;

namespace Wlrix.Packages.Tests;

public class PacmanOutputTests
{
    [Fact]
    public void ParseSearch_ReadsRepositoryNameVersionAndDescription()
    {
        var packages = PacmanOutput.ParseSearch(Fixture.Read("pacman-search.txt"));

        Assert.Equal(4, packages.Count);

        var first = packages[0];
        Assert.Equal("bash", first.Name);
        Assert.Equal("5.3.15-2", first.Version);
        Assert.Equal("cachyos-znver4", first.Repository);
        Assert.Equal("The GNU Bourne Again shell", first.Summary);
    }

    [Fact]
    public void ParseSearch_BareInstalledMarkerMeansTheOfferedVersionIsTheInstalledOne()
    {
        // "cachyos-znver4/bash 5.3.15-2 [installed]"
        var package = PacmanOutput.ParseSearch(Fixture.Read("pacman-search.txt"))[0];

        Assert.Equal("5.3.15-2", package.InstalledVersion);
        Assert.Equal(PackageStatus.SameVersion, package.Status);
        Assert.False(package.CanInstall);
        Assert.True(package.CanRemove);
    }

    [Fact]
    public void ParseSearch_InstalledMarkerWithAVersionMeansTheTwoDiffer()
    {
        // "core/bash 5.3.15-1 [installed: 5.3.15-2]" -- the repository offers -1, the system
        // has -2 from another repository. The row has to offer to install the other one.
        var package = PacmanOutput.ParseSearch(Fixture.Read("pacman-search.txt"))[2];

        Assert.Equal("core", package.Repository);
        Assert.Equal("5.3.15-1", package.Version);
        Assert.Equal("5.3.15-2", package.InstalledVersion);
        Assert.Equal(PackageStatus.UpgradeAvailable, package.Status);
        Assert.True(package.CanInstall);
    }

    [Fact]
    public void ParseSearch_NoMarkerMeansNotInstalled()
    {
        var packages = PacmanOutput.ParseSearch("""
            extra/cowsay 3.8.4-2
                Configurable speaking cow
            """);

        var package = Assert.Single(packages);
        Assert.Null(package.InstalledVersion);
        Assert.Equal(PackageStatus.New, package.Status);
        Assert.True(package.CanInstall);
        Assert.False(package.CanRemove);
    }

    [Fact]
    public void ParseSearch_ReadsAGroupListWithoutMistakingItForAVersion()
    {
        var packages = PacmanOutput.ParseSearch("""
            extra/gcc 15.1.1-3 (base-devel) [installed]
                The GNU Compiler Collection
            """);

        var package = Assert.Single(packages);
        Assert.Equal("gcc", package.Name);
        Assert.Equal("15.1.1-3", package.Version);
        Assert.Equal("15.1.1-3", package.InstalledVersion);
    }

    [Fact]
    public void ParseSearch_JoinsAWrappedDescription()
    {
        var packages = PacmanOutput.ParseSearch("""
            extra/example 1.0-1
                A description that the terminal
                wrapped onto a second line
            """);

        Assert.Equal("A description that the terminal wrapped onto a second line",
            Assert.Single(packages).Summary);
    }

    [Fact]
    public void ParseInstalled_ReadsNameAndVersion()
    {
        var packages = PacmanOutput.ParseInstalled("""
            a52dec 0.8.0-3.1
            aalib 1.4rc5-19.1
            """);

        Assert.Equal(2, packages.Count);
        Assert.Equal("a52dec", packages[0].Name);
        Assert.Equal("0.8.0-3.1", packages[0].Version);
        Assert.Equal(PackageStatus.Installed, packages[0].Status);
        Assert.True(packages[0].CanRemove);
    }

    [Fact]
    public void ParseUpdates_ReadsBothSidesOfTheArrow()
    {
        var packages = PacmanOutput.ParseUpdates("bash 5.3.15-2 -> 5.3.16-1");

        var package = Assert.Single(packages);
        Assert.Equal("bash", package.Name);
        Assert.Equal("5.3.15-2", package.InstalledVersion);
        Assert.Equal("5.3.16-1", package.Version);
        Assert.Equal(PackageStatus.UpgradeAvailable, package.Status);
    }

    [Fact]
    public void ParseDetails_ReadsTheFieldBlock()
    {
        var details = PacmanOutput.ParseDetails(Fixture.Read("pacman-si.txt"));

        var bash = details["bash"];
        Assert.Equal("5.3.15-2", bash.Version);
        Assert.Equal("The GNU Bourne Again shell", bash.Description);
        Assert.Equal("GPL-3.0-or-later", bash.License);
        Assert.Equal("https://www.gnu.org/software/bash/bash.html", bash.Url);
        Assert.Equal("cachyos-znver4", bash.Repository);
        Assert.Contains("readline", bash.Dependencies);
        Assert.Contains("glibc", bash.Dependencies);
    }

    [Fact]
    public void ParseDetails_KeepsTheFirstBlockWhenSeveralRepositoriesCarryThePackage()
    {
        // The fixture is `pacman -Si bash` on a CachyOS machine: cachyos-znver4 5.3.15-2 first,
        // then core 5.3.15-1. The first is the one an install would take, so it is the one the
        // details view has to show.
        var details = PacmanOutput.ParseDetails(Fixture.Read("pacman-si.txt"));

        Assert.Equal("cachyos-znver4", details["bash"].Repository);
        Assert.Equal("5.3.15-2", details["bash"].Version);
    }

    [Fact]
    public void ParseDetails_SplitsSeveralPackagesOnTheBlankLineBetweenThem()
    {
        var details = PacmanOutput.ParseDetails(Fixture.Read("pacman-info.txt"));

        Assert.Equal(2, details.Count);
        Assert.Contains("bash", details.Keys);
        Assert.Contains("zsh", details.Keys);
    }

    [Fact]
    public void ParseDetails_ConvertsTheHumanReadableInstalledSize()
    {
        var details = PacmanOutput.ParseDetails(Fixture.Read("pacman-info.txt"));

        // "Installed Size : 9.68 MiB"
        Assert.Equal(9912, details["bash"].InstalledSizeKilobytes);
    }

    [Fact]
    public void ParseDetails_TreatsNoneAsEmptyRatherThanAsAValue()
    {
        var details = PacmanOutput.ParseDetails("""
            Name            : example
            Version         : 1.0-1
            Licenses        : None
            Depends On      : None
            """);

        Assert.Equal(string.Empty, details["example"].License);
        Assert.Empty(details["example"].Dependencies);
    }

    [Fact]
    public void ParseDetails_ReadsQiWhichNamesTheRepositoryDifferently()
    {
        // -Si says "Repository"; -Qi says "Installed From", and puts it before Name.
        var details = PacmanOutput.ParseDetails("""
            Installed From  : cachyos-znver4
            Name            : bash
            Version         : 5.3.15-2
            """);

        Assert.Equal("cachyos-znver4", details["bash"].Repository);
    }

    [Fact]
    public void ParseConfiguration_ListsRepositoriesAndSkipsTheOptionsBlock()
    {
        var repositories = PacmanOutput.ParseConfiguration("""
            [options]
            HoldPkg = pacman glibc
            Architecture = auto

            [core]
            Include = /etc/pacman.d/mirrorlist

            [extra]
            Include = /etc/pacman.d/mirrorlist
            """);

        Assert.Equal(2, repositories.Count);
        Assert.Equal("core", repositories[0].Id);
        Assert.Equal("/etc/pacman.d/mirrorlist", repositories[0].Url);
        Assert.True(repositories[0].IsEnabled);
    }

    [Fact]
    public void ParseConfiguration_ReportsACommentedSectionAsDisabledRatherThanAbsent()
    {
        // The stock pacman.conf ships [multilib] commented out. A user looking for it wants to
        // see it listed and switched off, not missing entirely.
        var repositories = PacmanOutput.ParseConfiguration("""
            [core]
            Include = /etc/pacman.d/mirrorlist

            #[multilib]
            #Include = /etc/pacman.d/mirrorlist
            """);

        Assert.Equal(2, repositories.Count);

        var multilib = repositories[1];
        Assert.Equal("multilib", multilib.Id);
        Assert.False(multilib.IsEnabled);
        Assert.Equal("/etc/pacman.d/mirrorlist", multilib.Url);
    }

    [Fact]
    public void ParseConfiguration_DoesNotMistakePacmansOwnDocumentationForARepository()
    {
        // The stock pacman.conf explains its own format in a comment block, indented after the
        // '#'. Reading that as a disabled repository put "repo-name / ServerName" at the top of
        // the Repositories window. A real disabled entry is written hard against the '#'.
        var repositories = PacmanOutput.ParseConfiguration("""
            #
            # Repository entries are of the format:
            #       [repo-name]
            #       Server = ServerName
            #       Include = IncludePath
            #

            [core]
            Include = /etc/pacman.d/mirrorlist

            #[multilib]
            #Include = /etc/pacman.d/mirrorlist
            """);

        Assert.Equal(2, repositories.Count);
        Assert.Equal("core", repositories[0].Id);
        Assert.Equal("multilib", repositories[1].Id);
        Assert.DoesNotContain(repositories, repository => repository.Id == "repo-name");
    }

    [Fact]
    public void ParseConfiguration_PrefersAnExplicitServerOverAnIncludedMirrorList()
    {
        var repositories = PacmanOutput.ParseConfiguration("""
            [custom]
            Server = https://example.invalid/$repo/os/$arch
            """);

        Assert.Equal("https://example.invalid/$repo/os/$arch", Assert.Single(repositories).Url);
    }
}
