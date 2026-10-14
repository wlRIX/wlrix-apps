using Wlrix.Settings.Login.Core;
using Xunit;

namespace Wlrix.Settings.Login.Tests;

/// <summary>
/// The parse the root helper runs on its own argv. What it accepts is what reaches a file every
/// login reads, so the refusals matter as much as the round trip.
/// </summary>
public class GreeterRequestTests
{
    private static readonly GreeterConfig Full = new()
    {
        Palette = "gotham",
        Rules = "evdev",
        Model = "jp106",
        Layout = "jp,us",
        Variant = ",intl",
        Options = "grp:alt_shift_toggle",
        RepeatDelay = 600,
        RepeatRate = 25,
        Outputs =
        [
            new GreeterOutput("DP-1", null, new GreeterMode(2560, 1440, 144), 0, 0, 1.25, "90"),
            new GreeterOutput("HDMI-A-1", false),
        ],
    };

    private static bool Parse(params string[] args) => GreeterRequest.TryParse(args, out _, out _);

    [Fact]
    public void AWriteRoundTripsThroughItsArguments()
    {
        Assert.True(GreeterRequest.TryCreate(Full, out var request, out var error), error);
        Assert.True(GreeterRequest.TryParse(request.ToArguments(), out var parsed, out error), error);
        Assert.Equal(GreeterVerb.Write, parsed.Verb);
        Assert.Equal(Full, parsed.Config);
    }

    [Fact]
    public void AnEmptyWriteIsAllowed() => Assert.True(Parse("write"));

    [Fact]
    public void ResetTakesNothing()
    {
        Assert.True(GreeterRequest.TryParse(["reset"], out var request, out _));
        Assert.Equal(GreeterVerb.Reset, request.Verb);
        Assert.False(Parse("reset", "/etc/passwd"));
    }

    [Theory]
    [InlineData("--palette", "Gotham")]
    [InlineData("--palette", "../../x")]
    [InlineData("--layout", "jp\"\n[cursor]")]
    [InlineData("--layout", "jp us")]
    [InlineData("--options", "")]
    [InlineData("--repeat-delay", "-1")]
    [InlineData("--repeat-delay", "10001")]
    [InlineData("--repeat-rate", "256")]
    [InlineData("--repeat-rate", "fast")]
    [InlineData("--path", "/etc/shadow")]
    public void BadValuesAreRefused(string flag, string value) => Assert.False(Parse("write", flag, value));

    [Theory]
    [InlineData("enabled=true")]
    [InlineData("name=DP 1")]
    [InlineData("name=-DP-1")]
    [InlineData("name=DP-1,mode=1920x1080@60.5")]
    [InlineData("name=DP-1,mode=0x1080")]
    [InlineData("name=DP-1,mode=99999x1080")]
    [InlineData("name=DP-1,scale=0.1")]
    [InlineData("name=DP-1,scale=9")]
    [InlineData("name=DP-1,transform=45")]
    [InlineData("name=DP-1,position=0")]
    [InlineData("name=DP-1,position=0:40000")]
    [InlineData("name=DP-1,enabled=yes")]
    [InlineData("name=DP-1,name=DP-2")]
    [InlineData("name=DP-1,hdr=true")]
    public void BadOutputsAreRefused(string spec) => Assert.False(Parse("write", "--output", spec));

    [Fact]
    public void AFlagGivenTwiceIsRefused() =>
        Assert.False(Parse("write", "--layout", "us", "--layout", "jp"));

    [Fact]
    public void TheSameOutputTwiceIsRefused() =>
        Assert.False(Parse("write", "--output", "name=DP-1", "--output", "name=DP-1,enabled=false"));

    [Fact]
    public void AFlagWithoutAValueIsRefused() => Assert.False(Parse("write", "--layout"));

    [Fact]
    public void TooManyOutputsAreRefused()
    {
        var args = new List<string> { "write" };
        for (var i = 0; i <= GreeterRequest.MaximumOutputs; i++)
            args.AddRange(["--output", $"name=DP-{i}"]);
        Assert.False(GreeterRequest.TryParse(args, out _, out _));
    }

    [Fact]
    public void ScaleIsKeptToTheThousandth()
    {
        Assert.True(GreeterRequest.TryParse(["write", "--output", "name=DP-1,scale=1.33333"], out var request, out _));
        Assert.Equal(1.333, request.Config.Outputs[0].Scale);
    }

    [Theory]
    [InlineData("1920x1080@60", 1920, 1080, 60)]
    [InlineData("1920x1080@59.94", 1920, 1080, 60)]
    [InlineData("1920x1080@144Hz", 1920, 1080, 144)]
    [InlineData("1920x1080", 1920, 1080, null)]
    public void ModesParseAsTheCompositorReadsThem(string text, int width, int height, int? hz) =>
        Assert.Equal(new GreeterMode(width, height, hz), GreeterMode.Parse(text));
}
