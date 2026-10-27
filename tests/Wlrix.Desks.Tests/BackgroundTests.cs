using Avalonia.Media;
using Wlrix.Desks.Models;
using Wlrix.Desks.Services;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That a snapshot's background is what the desktop shows: <c>background.toml</c> read by
/// <c>wlrix-bg</c>'s rules, and a desk's own background as the compositor relays it.
/// </summary>
/// <remarks>
/// The cases are <c>wlrix-bg</c>'s own tests in <c>config.rs</c>, so the two readers are held to
/// the same answers.
/// </remarks>
public class BackgroundTests
{
    [Fact]
    public void NoFileIsAPlainGrayDesktop()
    {
        var wallpaper = SharedBackground.Parse("").Resolve(null);
        Assert.Null(wallpaper.Image);
        Assert.Equal(WallpaperMode.Fill, wallpaper.Mode);
        Assert.Equal(Color.FromRgb(0x55, 0x55, 0x55), wallpaper.Color);
    }

    [Fact]
    public void AnOutputOverridesFieldByField()
    {
        var shared = SharedBackground.Parse("""
            image = "/pictures/scatter.png"
            mode = "tile"
            color = "#555555"

            [[output]]
            name = "DP-1"
            image = "/pictures/one.sgi"
            mode = "center"
            color = "#000"

            [[output]]
            name = "HDMI-A-1"
            mode = "stretch"
            """);

        Assert.Equal(new Wallpaper("/pictures/one.sgi", WallpaperMode.Center, Colors.Black), shared.Resolve("DP-1"));
        Assert.Equal(
            new Wallpaper("/pictures/scatter.png", WallpaperMode.Stretch, Color.FromRgb(0x55, 0x55, 0x55)),
            shared.Resolve("HDMI-A-1"));
        // An unnamed monitor, or one with no section, takes the top level.
        Assert.Equal(WallpaperMode.Tile, shared.Resolve(null).Mode);
        Assert.Equal(WallpaperMode.Tile, shared.Resolve("eDP-1").Mode);
    }

    [Fact]
    public void AnEmptyImageInAnOverrideMeansNoPictureThere()
    {
        var shared = SharedBackground.Parse("""
            image = "/pictures/scatter.png"

            [[output]]
            name = "DP-2"
            image = ""
            """);

        Assert.Null(shared.Resolve("DP-2").Image);
        Assert.NotNull(shared.Resolve("DP-1").Image);
    }

    [Fact]
    public void SolidShowsNoPicture()
    {
        var shared = SharedBackground.Parse("""
            image = "/pictures/scatter.png"
            mode = "solid"
            """);

        Assert.Null(shared.Resolve(null).Image);
    }

    [Fact]
    public void AFileThatDoesNotParseIsTheDefault()
    {
        Assert.Equal(Wallpaper.Default, SharedBackground.Parse("image = ").Resolve(null));
    }

    [Fact]
    public void ADesksOwnBackgroundIsReadAsTheCompositorSendsIt()
    {
        Assert.Equal(
            new Wallpaper("/pictures/desk.rgb", WallpaperMode.Tile, Color.FromRgb(0x22, 0xee, 0x55)),
            Wallpaper.Parse("/pictures/desk.rgb", "tile", "#2e5"));
        Assert.Null(Wallpaper.Parse("", "fit", "#123456")!.Image);
        Assert.Null(Wallpaper.Parse("/pictures/desk.rgb", "solid", "#123456")!.Image);
    }

    [Theory]
    [InlineData("zoom", "#000000")]
    [InlineData("Fill", "#000000")]
    [InlineData("fill", "black")]
    [InlineData("fill", "#00000g")]
    [InlineData("fill", "#0000")]
    public void ABackgroundWlrixBgWouldRefuseIsNotRead(string mode, string color)
    {
        Assert.Null(Wallpaper.Parse("", mode, color));
    }
}
