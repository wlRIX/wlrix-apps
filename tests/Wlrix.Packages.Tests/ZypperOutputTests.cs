using Wlrix.Packages.Backends.Parsing;
using Wlrix.Packages.Models;
using Xunit;

namespace Wlrix.Packages.Tests;

public class ZypperOutputTests
{
    [Fact]
    public void ParseSearch_ReadsSolvablesAndTheirInstalledState()
    {
        var packages = ZypperOutput.ParseSearch("""
            <?xml version='1.0'?>
            <stream>
            <search-result version="0.0">
            <solvable-list>
            <solvable status="installed" name="bash" kind="package" edition="5.2.21-2.1"
                      arch="x86_64" repository="Main Repository" summary="The GNU Bourne Again Shell"/>
            <solvable status="not-installed" name="cowsay" kind="package" edition="3.04-1.5"
                      arch="noarch" repository="Main Repository" summary="Configurable talking cow"/>
            </solvable-list>
            </search-result>
            </stream>
            """);

        Assert.Equal(2, packages.Count);

        Assert.Equal("bash", packages[0].Name);
        Assert.Equal("5.2.21-2.1", packages[0].Version);
        Assert.Equal("5.2.21-2.1", packages[0].InstalledVersion);
        Assert.Equal("Main Repository", packages[0].Repository);
        Assert.Equal(PackageStatus.SameVersion, packages[0].Status);

        Assert.Null(packages[1].InstalledVersion);
        Assert.Equal(PackageStatus.New, packages[1].Status);
        Assert.True(packages[1].CanInstall);
    }

    [Fact]
    public void ParseSearch_DropsSolvablesThatAreNotPackages()
    {
        // A search also matches patterns, products and srcpackages. None of them is a thing
        // this window installs.
        var packages = ZypperOutput.ParseSearch("""
            <stream><search-result><solvable-list>
            <solvable status="not-installed" name="devel_basis" kind="pattern" edition="20230101"/>
            <solvable status="not-installed" name="gcc" kind="package" edition="13.2"/>
            </solvable-list></search-result></stream>
            """);

        Assert.Equal("gcc", Assert.Single(packages).Name);
    }

    [Fact]
    public void ParseUpdates_ReadsBothEditionsAndTheSourceAlias()
    {
        var packages = ZypperOutput.ParseUpdates("""
            <stream><update-status><update-list>
            <update name="bash" kind="package" edition="5.2.21-3.1" edition-old="5.2.21-2.1"
                    summary="The GNU Bourne Again Shell">
              <source url="http://download.opensuse.org/tumbleweed/repo/oss/" alias="repo-oss"/>
            </update>
            </update-list></update-status></stream>
            """);

        var package = Assert.Single(packages);
        Assert.Equal("bash", package.Name);
        Assert.Equal("5.2.21-2.1", package.InstalledVersion);
        Assert.Equal("5.2.21-3.1", package.Version);
        Assert.Equal("repo-oss", package.Repository);
        Assert.Equal(PackageStatus.UpgradeAvailable, package.Status);
    }

    [Fact]
    public void ParseRepositories_ReadsTheAliasNameEnabledFlagAndChildUrl()
    {
        var repositories = ZypperOutput.ParseRepositories("""
            <stream><repo-list>
            <repo alias="repo-oss" name="Main Repository" type="rpm-md" enabled="1"
                  autorefresh="1" gpgcheck="1" priority="99">
              <url>http://download.opensuse.org/tumbleweed/repo/oss/</url>
            </repo>
            <repo alias="repo-debug" name="Debug Repository" type="rpm-md" enabled="0"
                  autorefresh="1" gpgcheck="1" priority="99">
              <url>http://download.opensuse.org/debug/tumbleweed/repo/oss/</url>
            </repo>
            </repo-list></stream>
            """);

        Assert.Equal(2, repositories.Count);
        Assert.Equal("repo-oss", repositories[0].Id);
        Assert.Equal("Main Repository", repositories[0].Name);
        Assert.Equal("http://download.opensuse.org/tumbleweed/repo/oss/", repositories[0].Url);
        Assert.True(repositories[0].IsEnabled);
        Assert.False(repositories[1].IsEnabled);
    }

    [Fact]
    public void ParseRepositories_ReturnsNothingWhenTheOutputIsNotXml()
    {
        // zypper prints warnings outside its XML root when a repository is stale, which makes
        // the document unparseable. That is an empty listing, not an exception through the UI.
        Assert.Empty(ZypperOutput.ParseRepositories(
            "Warning: Repository 'repo-oss' appears to be outdated.\n<stream><repo-list>"));
    }

    [Fact]
    public void ParseDetails_ReadsTheHumanReadableInfoBlock()
    {
        var details = ZypperOutput.ParseDetails("""
            Loading repository data...
            Reading installed packages...

            Information for package bash:
            -----------------------------
            Repository     : Main Repository
            Name           : bash
            Version        : 5.2.21-2.1
            Arch           : x86_64
            Installed Size : 1.4 MiB
            Installed      : Yes
            Status         : up-to-date
            Source package : bash-5.2.21-2.1.src
            Upstream URL   : https://www.gnu.org/software/bash/
            Summary        : The GNU Bourne Again Shell
            Description    : Bash is the GNU Project's shell.
            """);

        Assert.NotNull(details);
        Assert.Equal("bash", details.Name);
        Assert.Equal("5.2.21-2.1", details.Version);
        Assert.Equal("Main Repository", details.Repository);
        Assert.Equal("https://www.gnu.org/software/bash/", details.Url);
        Assert.Equal(1434, details.InstalledSizeKilobytes);
        Assert.Equal("Bash is the GNU Project's shell.", details.Description);
    }

    [Fact]
    public void ParseDetails_ReturnsNullWhenThereIsNoPackageInTheOutput()
    {
        Assert.Null(ZypperOutput.ParseDetails("package 'nosuchthing' not found."));
    }
}
