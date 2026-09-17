using Wlrix.Files.Core.Metadata;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <remarks>
/// Modes are written in binary and grouped in threes, because C# has no octal literal and
/// <c>0b111_101_101</c> is the same three digits as <c>755</c> read one group at a time. The
/// octal spelling still appears, as the string the parser is given.
/// </remarks>
public class UnixPermissionsTests
{
    [Theory]
    [InlineData(0b111_101_101, "rwxr-xr-x")]
    [InlineData(0b110_100_100, "rw-r--r--")]
    [InlineData(0b110_000_000, "rw-------")]
    [InlineData(0, "---------")]
    [InlineData(0b111_111_111, "rwxrwxrwx")]
    public void ThePermissionsReadTheWayLsPrintsThem(int mode, string expected) =>
        Assert.Equal(expected, new UnixPermissions(mode).Symbolic);

    [Fact]
    public void ASpecialBitTakesOverTheExecuteColumnAndItsCaseSaysWhetherExecuteIsSetToo()
    {
        // The case is the whole message. "rws" is a setuid program; "rwS" is a setuid
        // program nobody can run, which is almost always a mistake, and nothing else in the
        // display would say so.
        Assert.Equal("rwsr-xr-x", new UnixPermissions(0b100_111_101_101).Symbolic);
        Assert.Equal("rwSr-xr-x", new UnixPermissions(0b100_110_101_101).Symbolic);
        Assert.Equal("rwxr-sr-x", new UnixPermissions(0b010_111_101_101).Symbolic);
        Assert.Equal("rwxr-Sr-x", new UnixPermissions(0b010_111_100_101).Symbolic);
        Assert.Equal("rwxrwxrwt", new UnixPermissions(0b001_111_111_111).Symbolic);
        Assert.Equal("rwxrwxrwT", new UnixPermissions(0b001_111_111_110).Symbolic);
    }

    [Fact]
    public void TheOctalFormIsFourDigitsSoTheSpecialBitsAreAlwaysVisible()
    {
        Assert.Equal("0755", new UnixPermissions(0b111_101_101).Octal);
        Assert.Equal("0644", new UnixPermissions(0b110_100_100).Octal);
        Assert.Equal("1777", new UnixPermissions(0b001_111_111_111).Octal);
        Assert.Equal("0000", new UnixPermissions(0).Octal);
    }

    [Fact]
    public void TheFileTypeBitsAboveTheModeAreDroppedRatherThanDisplayed()
    {
        // A mode straight from stat carries S_IFREG or S_IFDIR in the high bits. They are
        // not permissions, they are not editable, and passing them back to chmod is at best
        // ignored. The kind is FileKind's job.
        Assert.Equal(0b111_101_101, new UnixPermissions(0b001_000_000_111_101_101).Mode);
        Assert.Equal("rwxr-xr-x", new UnixPermissions(0b100_000_111_101_101).Symbolic);
    }

    [Fact]
    public void TheKindCharacterInFrontComesFromTheKindAndNotTheMode()
    {
        Assert.Equal("drwxr-xr-x", new UnixPermissions(0b111_101_101).Describe(FileKind.Directory));
        Assert.Equal("-rw-r--r--", new UnixPermissions(0b110_100_100).Describe(FileKind.File));
        Assert.Equal("lrwxrwxrwx", new UnixPermissions(0b111_111_111).Describe(FileKind.Symlink));
    }

    [Theory]
    [InlineData(PermissionClass.Owner, PermissionBits.Read, true)]
    [InlineData(PermissionClass.Owner, PermissionBits.Execute, true)]
    [InlineData(PermissionClass.Group, PermissionBits.Write, false)]
    [InlineData(PermissionClass.Other, PermissionBits.Read, true)]
    [InlineData(PermissionClass.Other, PermissionBits.Write, false)]
    public void EachCheckboxReadsItsOwnBit(PermissionClass who, PermissionBits what, bool expected) =>
        Assert.Equal(expected, new UnixPermissions(0b111_101_101).Has(who, what));

    [Fact]
    public void TogglingOneBitLeavesEveryOtherAlone()
    {
        var start = new UnixPermissions(0b110_100_100);

        var executable = start.With(PermissionClass.Owner, PermissionBits.Execute, true);
        Assert.Equal(0b111_100_100, executable.Mode);

        var closed = executable.With(PermissionClass.Other, PermissionBits.Read, false);
        Assert.Equal(0b111_100_000, closed.Mode);

        // Turning off something that was already off changes nothing, so a checkbox that
        // reports its state on load cannot corrupt the mode.
        Assert.Equal(closed, closed.With(PermissionClass.Other, PermissionBits.Read, false));
    }

    [Fact]
    public void TheSpecialBitsToggleWithoutDisturbingThePermissions()
    {
        var start = new UnixPermissions(0b111_101_101);
        Assert.Equal(0b100_111_101_101, start.WithSetUserId(true).Mode);
        Assert.Equal(0b010_111_101_101, start.WithSetGroupId(true).Mode);
        Assert.Equal(0b001_111_101_101, start.WithSticky(true).Mode);
        Assert.Equal(0b111_101_101, start.WithSetUserId(true).WithSetUserId(false).Mode);
    }

    [Theory]
    [InlineData("755", 0b111_101_101)]
    [InlineData("0755", 0b111_101_101)]
    [InlineData("644", 0b110_100_100)]
    [InlineData("1777", 0b001_111_111_111)]
    [InlineData("0", 0)]
    [InlineData("  755  ", 0b111_101_101)]
    public void AnOctalModeCanBeTypedIn(string text, int expected)
    {
        Assert.True(UnixPermissions.TryParseOctal(text, out var parsed));
        Assert.Equal(expected, parsed.Mode);
    }

    [Theory]
    [InlineData("u+x")]      // chmod's symbolic form, which this deliberately does not speak
    [InlineData("799")]      // a digit that is not octal
    [InlineData("07555")]    // more bits than there are
    [InlineData("rwxr-xr-x")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingThatIsNotAnOctalModeIsRefusedRatherThanGuessedAt(string? text) =>
        Assert.False(UnixPermissions.TryParseOctal(text, out _));

    [Fact]
    public void EverySpellingOfAModeAgreesWithEveryOther()
    {
        // The dialog shows all three at once — checkboxes, the octal box and the ls line —
        // and they are all derived from the same twelve bits, so they cannot drift.
        for (var mode = 0; mode <= 0xFFF; mode++)
        {
            var permissions = new UnixPermissions(mode);
            Assert.True(UnixPermissions.TryParseOctal(permissions.Octal, out var reparsed));
            Assert.Equal(permissions, reparsed);

            var rebuilt = new UnixPermissions(0)
                .WithSetUserId(permissions.SetUserId)
                .WithSetGroupId(permissions.SetGroupId)
                .WithSticky(permissions.Sticky);
            foreach (var who in new[] { PermissionClass.Owner, PermissionClass.Group, PermissionClass.Other })
            foreach (var what in new[] { PermissionBits.Read, PermissionBits.Write, PermissionBits.Execute })
                rebuilt = rebuilt.With(who, what, permissions.Has(who, what));

            Assert.Equal(permissions, rebuilt);
        }
    }
}
