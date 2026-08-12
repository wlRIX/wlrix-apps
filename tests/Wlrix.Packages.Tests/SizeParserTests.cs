using Wlrix.Packages.Backends.Parsing;
using Xunit;

namespace Wlrix.Packages.Tests;

public class SizeParserTests
{
    [Theory]
    [InlineData("1024.00 KiB", 1024)]
    [InlineData("9.68 MiB", 9912)]
    [InlineData("1.00 GiB", 1048576)]
    [InlineData("2.1 GB", 2202010)]
    public void ToKilobytes_ConvertsTheUnitsPackageManagersPrint(string text, long expected) =>
        Assert.Equal(expected, SizeParser.ToKilobytes(text));

    [Theory]
    [InlineData("512.00 B")]
    [InlineData("1.00 B")]
    public void ToKilobytes_RoundsASubKilobyteSizeUpRatherThanAwayToUnknown(string text) =>
        // Zero is reserved for "the package manager did not say". A tiny package rounding down
        // to zero would have the Size column claim not to know a size it was just told.
        Assert.Equal(1, SizeParser.ToKilobytes(text));

    [Fact]
    public void ToKilobytes_TreatsABareNumberAsKilobytes() =>
        // dpkg's Installed-Size field, which its manual documents as kilobytes.
        Assert.Equal(6470, SizeParser.ToKilobytes("6470"));

    [Fact]
    public void ToKilobytes_IgnoresAThousandsSeparator() =>
        Assert.Equal(1234567, SizeParser.ToKilobytes("1,234,567"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("None")]
    public void ToKilobytes_AnswersZeroForAnythingThatIsNotASize(string? text) =>
        // Zero is this library's "the package manager did not say". A size is a column in a
        // list, never a reason to fail the listing it belongs to.
        Assert.Equal(0, SizeParser.ToKilobytes(text));
}
