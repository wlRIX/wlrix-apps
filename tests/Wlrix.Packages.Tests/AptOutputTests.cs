using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Xunit;

namespace Wlrix.Packages.Tests;

public class AptOutputTests
{
    [Fact]
    public void ParseInstalled_ReadsTheTabSeparatedDpkgQueryFormat()
    {
        var packages = AptOutput.ParseInstalled(
            "ii \tbash\t5.2.15-2+b7\t6470\tGNU Bourne Again SHell\n" +
            "ii \tcoreutils\t9.1-1\t18124\tGNU core utilities\n");

        Assert.Equal(2, packages.Count);
        Assert.Equal("bash", packages[0].Name);
        Assert.Equal("5.2.15-2+b7", packages[0].Version);
        Assert.Equal("GNU Bourne Again SHell", packages[0].Summary);
        Assert.Equal(6470, packages[0].InstalledSizeKilobytes);
        Assert.Equal(PackageStatus.Installed, packages[0].Status);
    }

    [Fact]
    public void ParseInstalled_SkipsPackagesThatAreRemovedButStillConfigured()
    {
        // dpkg keeps an "rc" entry for a purged-but-not-removed package. It is not installed
        // software, and listing it would show the user something they cannot find anywhere else
        // on their system.
        var packages = AptOutput.ParseInstalled(
            "ii \tbash\t5.2.15-2\t6470\tGNU Bourne Again SHell\n" +
            "rc \tnano\t7.2-1\t900\tsmall editor\n");

        Assert.Equal("bash", Assert.Single(packages).Name);
    }

    [Fact]
    public void ParseSearch_SplitsOnTheDashSeparator()
    {
        var hits = AptOutput.ParseSearch("""
            bash - GNU Bourne Again SHell
            bash-completion - programmable completion for the bash shell
            """);

        Assert.Equal(2, hits.Count);
        Assert.Equal("bash", hits[0].Name);
        Assert.Equal("GNU Bourne Again SHell", hits[0].Summary);
        Assert.Equal("programmable completion for the bash shell", hits[1].Summary);
    }

    [Fact]
    public void ParseShow_ReadsAStanzaIncludingItsIndentedDescription()
    {
        var stanzas = AptOutput.ParseShow("""
            Package: bash
            Version: 5.2.15-2+b7
            Installed-Size: 6470
            Section: shells
            Homepage: http://tiswww.case.edu/php/chet/bash/bashtop.html
            Depends: base-files (>= 2.1.12), debianutils (>= 5.6-0.1)
            Description: GNU Bourne Again SHell
             Bash is an sh-compatible command language interpreter.
             .
             It offers functional improvements over sh.
            """);

        var bash = stanzas["bash"];
        Assert.Equal("5.2.15-2+b7", bash.Version);
        Assert.Equal(6470, bash.SizeKilobytes);
        Assert.Equal("shells", bash.Section);
        Assert.Equal("GNU Bourne Again SHell", bash.Summary);
        Assert.Contains("sh-compatible", bash.Description);
        Assert.Contains("functional improvements", bash.Description);
    }

    [Fact]
    public void ParseShow_KeepsTheFirstStanzaWhenAPackageHasSeveralVersions()
    {
        // apt-cache prints the candidate first, which is what an install would take.
        var stanzas = AptOutput.ParseShow("""
            Package: bash
            Version: 5.2.15-2+b7

            Package: bash
            Version: 5.2.15-2
            """);

        Assert.Equal("5.2.15-2+b7", stanzas["bash"].Version);
    }

    [Fact]
    public void ParseSimulation_ReadsAnUpgradeWithBothVersions()
    {
        var packages = AptOutput.ParseSimulation(
            "Inst libc6 [2.36-9] (2.36-9+deb12u1 Debian:12.1/stable [amd64])\n" +
            "Conf libc6 (2.36-9+deb12u1 Debian:12.1/stable [amd64])\n");

        var package = Assert.Single(packages);
        Assert.Equal("libc6", package.Name);
        Assert.Equal("2.36-9", package.InstalledVersion);
        Assert.Equal("2.36-9+deb12u1", package.Version);
        Assert.Equal("Debian:12.1/stable", package.Repository);
        Assert.Equal(PackageStatus.UpgradeAvailable, package.Status);
    }

    [Fact]
    public void ParseSimulation_ReadsAFreshInstallAsNew()
    {
        var packages = AptOutput.ParseSimulation(
            "Inst cowsay (3.03+dfsg2-8 Debian:12.1/stable [all])\n");

        var package = Assert.Single(packages);
        Assert.Null(package.InstalledVersion);
        Assert.Equal("3.03+dfsg2-8", package.Version);
        Assert.Equal(PackageStatus.New, package.Status);
    }

    [Fact]
    public void ParseSimulation_IgnoresConfAndRemvLines()
    {
        // Only Inst lines describe something arriving. Conf is a second pass over the same
        // package, and Remv belongs to the removal half of a plan.
        var packages = AptOutput.ParseSimulation("""
            Remv obsolete-package [1.0-1]
            Conf libc6 (2.36-9+deb12u1 Debian:12.1/stable [amd64])
            """);

        Assert.Empty(packages);
    }

    [Fact]
    public void ParseSourcesList_ReadsTheOneLineFormat()
    {
        var repositories = AptOutput.ParseSourcesList("sources.list", """
            deb http://deb.debian.org/debian bookworm main contrib
            deb-src http://deb.debian.org/debian bookworm main
            """);

        Assert.Equal(2, repositories.Count);
        Assert.Equal("http://deb.debian.org/debian", repositories[0].Url);
        Assert.Equal("deb bookworm", repositories[0].Name);
        Assert.True(repositories[0].IsEnabled);
    }

    [Fact]
    public void ParseSourcesList_ReadsACommentedLineAsADisabledSource()
    {
        var repositories = AptOutput.ParseSourcesList("sources.list", """
            deb http://deb.debian.org/debian bookworm main
            # deb-src http://deb.debian.org/debian bookworm main
            """);

        Assert.Equal(2, repositories.Count);
        Assert.True(repositories[0].IsEnabled);
        Assert.False(repositories[1].IsEnabled);
    }

    [Fact]
    public void ParseSourcesList_SkipsPastBracketedOptionsToFindTheUri()
    {
        var repositories = AptOutput.ParseSourcesList("extra.list",
            "deb [signed-by=/usr/share/keyrings/example.gpg] https://example.invalid/apt stable main");

        var repository = Assert.Single(repositories);
        Assert.Equal("https://example.invalid/apt", repository.Url);
        Assert.Equal("deb stable", repository.Name);
    }

    [Fact]
    public void ParseDeb822Sources_ReadsAStanzaAndItsEnabledField()
    {
        var repositories = AptOutput.ParseDeb822Sources("debian.sources", """
            Types: deb
            URIs: http://deb.debian.org/debian
            Suites: bookworm bookworm-updates
            Components: main contrib

            Types: deb-src
            URIs: http://deb.debian.org/debian
            Suites: bookworm
            Enabled: no
            """);

        Assert.Equal(2, repositories.Count);
        Assert.True(repositories[0].IsEnabled);
        Assert.Equal("http://deb.debian.org/debian", repositories[0].Url);
        Assert.False(repositories[1].IsEnabled);
    }

    [Fact]
    public void ParseDeb822Sources_TreatsAnAbsentEnabledFieldAsEnabled()
    {
        var repositories = AptOutput.ParseDeb822Sources("debian.sources", """
            Types: deb
            URIs: http://deb.debian.org/debian
            Suites: bookworm
            """);

        Assert.True(Assert.Single(repositories).IsEnabled);
    }
}
