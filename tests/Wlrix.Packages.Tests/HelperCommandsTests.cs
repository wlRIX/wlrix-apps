using Wlrix.Packages.Privileged;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>
/// What each verb actually runs. These read like a table because they are one: the point is
/// that the mapping is visible and reviewable, not that it is clever.
/// </summary>
public class HelperCommandsTests
{
    private static HelperCommand? Command(string verb, string backend, params string[] targets)
    {
        Assert.True(HelperRequest.TryParse([verb, backend, .. targets], fileMustExist: false,
            out var request, out var error), error);

        return HelperCommands.For(request!);
    }

    [Theory]
    [InlineData("pacman", "pacman -S --noconfirm --needed -- cowsay")]
    [InlineData("apt", "apt-get install -y -- cowsay")]
    [InlineData("zypper", "zypper --non-interactive install -- cowsay")]
    public void Install(string backend, string expected) =>
        Assert.Equal(expected, Command("install", backend, "cowsay")!.ToString());

    [Theory]
    [InlineData("pacman", "pacman -Rs --noconfirm -- cowsay")]
    [InlineData("apt", "apt-get remove -y -- cowsay")]
    [InlineData("zypper", "zypper --non-interactive remove --clean-deps -- cowsay")]
    public void Remove(string backend, string expected) =>
        // pacman's -Rs and zypper's --clean-deps are the same intent: removing a package also
        // removes what only it needed. Without them "remove" leaves the dependency tree behind.
        Assert.Equal(expected, Command("remove", backend, "cowsay")!.ToString());

    [Theory]
    [InlineData("pacman", "pacman -Syu --noconfirm")]
    [InlineData("apt", "apt-get dist-upgrade -y")]
    [InlineData("zypper", "zypper --non-interactive update --type package")]
    public void Upgrade(string backend, string expected) =>
        Assert.Equal(expected, Command("upgrade", backend)!.ToString());

    [Fact]
    public void EveryTargetStaysASeparateArgument()
    {
        // The one property that matters most here: nothing is ever joined into a command line,
        // so a package name cannot become two arguments or an option.
        var command = Command("install", "pacman", "cowsay", "zsh")!;

        Assert.Equal(["-S", "--noconfirm", "--needed", "--", "cowsay", "zsh"], command.Arguments);
    }

    [Fact]
    public void EveryPackageManagerIsHandedADoubleDashBeforeItsTargets()
    {
        // Belt and braces over the name validation: even if a name that looks like an option got
        // through, "--" stops the package manager reading it as one.
        foreach (var backend in new[] { "pacman", "apt", "zypper" })
        {
            var command = Command("install", backend, "cowsay")!;
            var separator = command.Arguments.ToList().IndexOf("--");

            Assert.True(separator >= 0, $"{backend} install has no -- separator");
            Assert.Equal("cowsay", command.Arguments[separator + 1]);
        }
    }

    [Fact]
    public void AptRunsWithTheNoninteractiveFrontend() =>
        // Without it a maintainer script opens a dialog on a terminal that is not there and
        // waits for an answer nobody is going to give -- as root, for ever.
        Assert.Equal("noninteractive",
            Command("install", "apt", "cowsay")!.Environment["DEBIAN_FRONTEND"]);

    [Fact]
    public void PacmanHasNoRepositoryCommands()
    {
        // Not an oversight: pacman has no such command, and the alternative is editing a
        // hand-owned pacman.conf on the user's behalf. See the Wlrix.Packages README.
        Assert.Null(Command("repoadd", "pacman", "custom", "https://example.invalid/repo"));
        Assert.Null(Command("reporemove", "pacman", "custom"));
        Assert.Null(Command("repoenable", "pacman", "custom"));
    }

    [Fact]
    public void ZypperTakesTheUrlBeforeTheAliasAsAddrepoWantsThem() =>
        Assert.Equal("zypper --non-interactive addrepo -- https://example.invalid/repo packman",
            Command("repoadd", "zypper", "packman", "https://example.invalid/repo")!.ToString());

    [Fact]
    public void AptHasNoEnableOrDisable() =>
        // A deb source is either present in a file or commented out in it; apt-add-repository
        // has no verb for the middle state.
        Assert.Null(Command("repoenable", "apt", "custom"));

    [Fact]
    public void InstallFileHandsThePathToTheResolverRatherThanTheUnpacker() =>
        // apt-get install with a path pulls the file's dependencies from the archive.
        // dpkg -i would install it and leave them unmet, which is the classic way to end up
        // with a broken system from one double-click.
        Assert.Equal("apt-get install -y -- /tmp/cowsay.deb",
            Command("installfile", "apt", "/tmp/cowsay.deb")!.ToString());
}
