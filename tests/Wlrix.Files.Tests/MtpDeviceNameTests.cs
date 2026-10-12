using Wlrix.Files.Services;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// Reading a phone's name out of udev, which escapes it the way it escapes everything.
/// </summary>
public sealed class MtpDeviceNameTests
{
    [Theory]
    [InlineData(@"Pixel\x20Fold", "Pixel Fold")]
    [InlineData(@"Galaxy\x20S24\x20\x20", "Galaxy S24")]
    [InlineData("Plain", "Plain")]
    // A name in UTF-8 arrives a byte at a time, and has to be put back together as one.
    [InlineData(@"\xe3\x83\x94\xe3\x82\xaf\xe3\x82\xbb\xe3\x83\xab", "ピクセル")]
    // A backslash that does not start an escape is just a backslash.
    [InlineData(@"A\B", @"A\B")]
    public void EscapesAreUndone(string encoded, string expected) =>
        Assert.Equal(expected, MtpDevices.Decode(encoded));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\x20\x20")]
    public void NothingUsefulIsNoName(string? encoded) =>
        Assert.Null(MtpDevices.Decode(encoded));
}
