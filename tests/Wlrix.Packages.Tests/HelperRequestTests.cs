using Wlrix.Packages.Privileged;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>
/// The validation gate in front of a program that runs as root. Everything here is a test that
/// something is <em>refused</em>, which is the direction that matters: a rule that wrongly
/// accepts is a hole, and a rule that wrongly rejects is a package the user installs from a
/// terminal instead.
/// </summary>
public class HelperRequestTests
{
    private static bool TryParse(string[] arguments, out HelperRequest? request, out string? error) =>
        HelperRequest.TryParse(arguments, fileMustExist: false, out request, out error);

    [Fact]
    public void TryParse_AcceptsAPlainInstall()
    {
        Assert.True(TryParse(["install", "pacman", "cowsay", "zsh"], out var request, out _));

        Assert.Equal("pacman", request!.Backend);
        Assert.Equal(HelperVerb.Install, request.Verb);
        Assert.Equal(["cowsay", "zsh"], request.Targets);
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveAboutTheVerbAndRoundTripsThroughArgv()
    {
        Assert.True(TryParse(["INSTALL", "apt", "cowsay"], out var request, out _));
        Assert.Equal(["install", "apt", "cowsay"], request!.ToArguments());
    }

    [Theory]
    [InlineData("dnf")]
    [InlineData("emerge")]
    [InlineData("")]
    [InlineData("pacman ")]
    public void TryParse_RejectsABackendItDoesNotKnow(string backend)
    {
        Assert.False(TryParse(["install", backend, "cowsay"], out _, out var error));
        Assert.Contains("unknown backend", error!);
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("run")]
    [InlineData("shell")]
    public void TryParse_RejectsAVerbThatIsNotOnTheList(string verb)
    {
        // There is no verb that takes a command to run, and this is the test that says so.
        Assert.False(TryParse([verb, "pacman", "anything"], out _, out var error));
        Assert.Contains("unknown verb", error!);
    }

    [Theory]
    [InlineData("--noconfirm")]        // an option wearing a package name's clothes
    [InlineData("-Rns")]
    [InlineData("cowsay; rm -rf /")]   // shell metacharacters, if there were ever a shell
    [InlineData("cowsay && reboot")]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("cow say")]
    [InlineData("../../etc/passwd")]
    [InlineData("cowsay\nzsh")]
    public void TryParse_RejectsAPackageNameThatIsNotOne(string name)
    {
        Assert.False(TryParse(["install", "pacman", name], out _, out var error));
        Assert.Contains("not a valid package name", error!);
    }

    [Fact]
    public void TryParse_KeepsControlCharactersOutOfTheMessageItReportsBack()
    {
        // The rejected argument goes into a message the user reads. An escape sequence in it
        // could rewrite the rest of the line in a terminal, so it never survives to be printed.
        Assert.False(TryParse(["install", "pacman", "evil[2Kname"], out _, out var error));
        Assert.DoesNotContain('', error!);
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("upgrade")]
    public void TryParse_RefusesTargetsOnAVerbThatTakesNone(string verb)
    {
        Assert.True(TryParse([verb, "pacman"], out _, out _));
        Assert.False(TryParse([verb, "pacman", "cowsay"], out _, out var error));
        Assert.Contains("takes no targets", error!);
    }

    [Fact]
    public void TryParse_RefusesAnInstallWithNothingToInstall()
    {
        Assert.False(TryParse(["install", "pacman"], out _, out var error));
        Assert.Contains("at least one target", error!);
    }

    [Theory]
    [InlineData("cowsay.deb")]                   // relative
    [InlineData("./cowsay.deb")]
    [InlineData("/tmp/../etc/shadow.deb")]       // absolute, but not a plain path
    [InlineData("/etc/passwd")]                  // absolute and plain, but not a package
    [InlineData("/tmp/script.sh")]
    public void TryParse_RejectsAFileThatIsNotAnAbsolutePathToAPackage(string path)
    {
        Assert.False(TryParse(["installfile", "apt", path], out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("/tmp/cowsay_3.03-1_all.deb")]
    [InlineData("/var/cache/pacman/pkg/cowsay-3.8.4-2-any.pkg.tar.zst")]
    [InlineData("/tmp/cowsay-3.04-1.noarch.rpm")]
    public void TryParse_AcceptsAnAbsolutePathToAPackageFile(string path)
    {
        Assert.True(TryParse(["installfile", "apt", path], out var request, out var error), error);
        Assert.Equal([path], request!.Targets);
    }

    [Fact]
    public void TryParse_ChecksThatAFileExistsWhenTheHelperAsksItTo()
    {
        // The application does not require the file to be there yet; the helper does, because it
        // is about to hand the path to a package manager running as root.
        var missing = new[] { "installfile", "apt", "/tmp/there-is-no-such-package.deb" };

        Assert.True(HelperRequest.TryParse(missing, fileMustExist: false, out _, out _));
        Assert.False(HelperRequest.TryParse(missing, fileMustExist: true, out _, out var error));
        Assert.Contains("does not exist", error!);
    }

    [Fact]
    public void TryParse_RequiresAnAliasAndAUrlForRepoAdd()
    {
        Assert.True(TryParse(["repoadd", "zypper", "packman", "https://example.invalid/repo"],
            out _, out var error), error);

        Assert.False(TryParse(["repoadd", "zypper", "packman"], out _, out _));
        Assert.False(TryParse(["repoadd", "zypper", "packman", "a", "b"], out _, out _));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void TryParse_RejectsARepositoryUrlThatIsNotHttpOrFtp(string url)
    {
        Assert.False(TryParse(["repoadd", "zypper", "alias", url], out _, out var error));
        Assert.Contains("not an http", error!);
    }

    [Fact]
    public void TryParse_RejectsAnImplausiblyLongArgument()
    {
        Assert.False(TryParse(["install", "pacman", new string('a', 1000)], out _, out var error));
        Assert.Contains("implausibly long", error!);
    }

    [Fact]
    public void TryParse_RejectsMoreTargetsThanAnyTransactionHas()
    {
        var many = new[] { "install", "pacman" }
            .Concat(Enumerable.Range(0, 600).Select(i => $"package{i}"))
            .ToArray();

        Assert.False(TryParse(many, out _, out var error));
        Assert.Contains("too many targets", error!);
    }
}
