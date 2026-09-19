using Wlrix.Desks;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That the command line means what the <c>ov</c> man page says it means.
/// </summary>
/// <remarks>
/// The whole of this change set that can be tested without a display or a session bus. The
/// alias table and the options are checked against each other rather than separately, because
/// the way these drift apart is one of them gaining a flag the other has never heard of.
/// </remarks>
public class CommandLineTests
{
    /// <summary>Every short form, with the long option it stands for.</summary>
    public static TheoryData<string, string> Aliases => new()
    {
        { "--nown", "--noWindowName" },
        { "--hss", "--hideSnapShots" },
        { "--ngd", "--noGlobalDesk" },
    };

    [Theory]
    [MemberData(nameof(Aliases))]
    public void AShortFormExpandsToItsLongOption(string alias, string expanded) =>
        Assert.Equal([expanded], DesksCommandLine.ExpandAliases([alias]));

    [Theory]
    [MemberData(nameof(Aliases))]
    public void AShortFormParsesToTheSameOptionsAsItsLongForm(string alias, string expanded)
    {
        // Through the expansion, which is the only route a short form ever takes. What this
        // catches is the alias table naming an option that does not exist -- a typo on the
        // right-hand side would otherwise turn a documented short form into a usage error.
        Assert.True(TryParse(DesksCommandLine.ExpandAliases([alias]), out var shortForm, out _, out _));
        Assert.True(TryParse([expanded], out var longForm, out _, out _));

        Assert.Equal(longForm.NoWindowName, shortForm.NoWindowName);
        Assert.Equal(longForm.HideSnapshots, shortForm.HideSnapshots);
        Assert.Equal(longForm.NoGlobalDesk, shortForm.NoGlobalDesk);
    }

    [Fact]
    public void ExpandingIsWholeTokens()
    {
        // A near miss must stay a near miss: expanding a prefix would turn a typo into a flag.
        Assert.Equal(["--nowned", "--hssx", "nown"], DesksCommandLine.ExpandAliases(["--nowned", "--hssx", "nown"]));
    }

    [Fact]
    public void ExpandingLeavesEverythingElseAlone() =>
        Assert.Equal(["--demo", "--new"], DesksCommandLine.ExpandAliases(["--demo", "--new"]));

    [Fact]
    public void NoArgumentsIsNotAnError()
    {
        Assert.True(TryParse([], out var options, out var error, out var exitCode));
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.False(options.NoWindowName);
        Assert.False(options.HideSnapshots);
        Assert.False(options.NoGlobalDesk);
        Assert.False(options.New);
        Assert.False(options.Demo);
    }

    [Fact]
    public void TheFlagsCombine()
    {
        Assert.True(TryParse(
            DesksCommandLine.ExpandAliases(["--hss", "--ngd", "--nown", "--new"]),
            out var options, out _, out _));

        Assert.True(options.HideSnapshots);
        Assert.True(options.NoGlobalDesk);
        Assert.True(options.NoWindowName);
        Assert.True(options.New);
    }

    [Fact]
    public void DemoStillParses()
    {
        // The one flag that existed before any of this, and the only way to run the UI without
        // a compositor.
        Assert.True(TryParse(["--demo"], out var options, out _, out _));
        Assert.True(options.Demo);
    }

    [Fact]
    public void TheManPagesCasingIsNotLoadBearing()
    {
        // `hideSnapShots` is IRIX's capitalization, not a shape anyone types correctly first go.
        Assert.True(TryParse(["--hidesnapshots"], out var options, out _, out _));
        Assert.True(options.HideSnapshots);
    }

    [Fact]
    public void AnUnknownOptionFailsLoudlyAndNamesItself()
    {
        // The house rule, and the reason for it: a flag a build has never heard of must not be
        // quietly ignored, or an old binary answers a question it did not understand.
        Assert.False(TryParse(["--bogus"], out _, out var error, out var exitCode));
        Assert.Equal(2, exitCode);
        Assert.Contains("--bogus", error, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpIsNotAnError()
    {
        Assert.False(TryParse(["--help"], out _, out var error, out var exitCode, out var output));
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("--hideSnapShots", output, StringComparison.Ordinal);
        // The short forms are not options, so this line is the only place they are documented.
        Assert.Contains("--hss", output, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionIsNotAnError()
    {
        Assert.False(TryParse(["--version"], out _, out var error, out var exitCode, out var output));
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Contains("wlrix-desks", output, StringComparison.Ordinal);
    }

    private static bool TryParse(string[] args, out DesksOptions options, out string error, out int exitCode) =>
        TryParse(args, out options, out error, out exitCode, out _);

    private static bool TryParse(
        string[] args, out DesksOptions options, out string error, out int exitCode, out string output)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var parsed = DesksCommandLine.TryParse(args, stdout, stderr, out options, out exitCode);
        output = stdout.ToString();
        error = stderr.ToString();
        return parsed;
    }
}
