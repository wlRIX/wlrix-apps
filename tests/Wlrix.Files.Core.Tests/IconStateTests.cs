using SkiaSharp;
using Wlrix.Icons;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// IRIX's icon state rule, pinned to the same cases as <c>wlrix-ui</c>'s <c>icon_state</c>
/// tests: the two have to agree, or a selected file reads differently here and on the desktop.
/// </summary>
public class IconStateTests
{
    private static readonly IconStateColors Colors = new(0xC9C9C9, 0xFFFFFF, 0xFFFF00, 0x555555, 0x0000FF);

    private static (byte, byte, byte, byte) Paint(IconState state, int x, int y, uint rgb, byte a = 255) =>
        IconStatePainter.Paint(state, Colors, x, y, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);

    [Fact]
    public void AWhiteBodyTakesTheStateColorOutright()
    {
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0x00, (byte)0xFF), Paint(IconState.Selected, 0, 0, 0xFFFFFF));
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF), Paint(IconState.Hover, 0, 0, 0xFFFFFF));
    }

    [Fact]
    public void AGhostIsDarkGrayAndADropTargetRoyalBlue()
    {
        // After the IRIX Interactive Desktop UI Guidelines' figure of icon states, as the
        // desktop draws them: solid, with no stipple.
        foreach (var (x, y) in new[] { (0, 0), (1, 0) })
        {
            Assert.Equal(((byte)0x55, (byte)0x55, (byte)0x55, (byte)0xFF), Paint(IconState.Ghost, x, y, 0xFFFFFF));
            Assert.Equal(((byte)0x00, (byte)0x00, (byte)0xFF, (byte)0xFF), Paint(IconState.DropTarget, x, y, 0xFFFFFF));
        }
        // And the outline is untouched, as in every other state.
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0xFF), Paint(IconState.DropTarget, 0, 0, 0x000000));
    }

    [Fact]
    public void SelectedWithoutTheKeyboardIsTheRestCheckerboardInYellow()
    {
        // Measured off IRIX: #ffff00 and #c9c9c9 alternating, in the rest state's phase.
        var yellow = ((byte)0xFF, (byte)0xFF, (byte)0x00, (byte)0xFF);
        var gray = ((byte)0xC9, (byte)0xC9, (byte)0xC9, (byte)0xFF);
        Assert.Equal(yellow, Paint(IconState.SelectedInactive, 0, 0, 0xFFFFFF));
        Assert.Equal(yellow, Paint(IconState.SelectedInactive, 1, 1, 0xFFFFFF));
        Assert.Equal(gray, Paint(IconState.SelectedInactive, 1, 0, 0xFFFFFF));
        Assert.Equal(gray, Paint(IconState.SelectedInactive, 0, 1, 0xFFFFFF));
    }

    [Fact]
    public void AtRestTheBodyIsACheckerboardOfWhiteAndTheRestGray()
    {
        var white = ((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF);
        var gray = ((byte)0xC9, (byte)0xC9, (byte)0xC9, (byte)0xFF);
        Assert.Equal(white, Paint(IconState.Normal, 0, 0, 0xFFFFFF));
        Assert.Equal(white, Paint(IconState.Normal, 1, 1, 0xFFFFFF));
        Assert.Equal(gray, Paint(IconState.Normal, 1, 0, 0xFFFFFF));
        Assert.Equal(gray, Paint(IconState.Normal, 0, 1, 0xFFFFFF));
    }

    [Theory]
    // Measured off IRIX: black and the #868686 shadow are the same in every state. The icon
    // theme's #808080 shadow, #999999 detail and the folder's tan are the same case.
    [InlineData(0x000000u)]
    [InlineData(0x868686u)]
    [InlineData(0x808080u)]
    [InlineData(0x999999u)]
    [InlineData(0xDECD87u)]
    [InlineData(0xE02020u)]
    public void TheOutlineTheShadowAndColoredDetailNeverChange(uint rgb)
    {
        var unchanged = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)0xFF);
        foreach (var state in new[] { IconState.Normal, IconState.Hover, IconState.Selected })
        {
            Assert.Equal(unchanged, Paint(state, 0, 0, rgb));
            Assert.Equal(unchanged, Paint(state, 1, 0, rgb));
        }
    }

    [Fact]
    public void AnOffWhiteBodyIsTintedAndKeepsItsShading()
    {
        // The icon theme's text file is #ffefe5. Selected it goes yellow, a shade off pure.
        var (r, g, b, _) = Paint(IconState.Selected, 0, 0, 0xFFEFE5);
        Assert.Equal(0xFF, r);
        Assert.Equal(0xEF, g);
        Assert.Equal(0x00, b);
    }

    [Fact]
    public void AHalfCoveredEdgeOfTheBodyKeepsItsAlpha()
    {
        var (_, _, b, a) = Paint(IconState.Selected, 0, 0, 0xFFFFFF, a: 0x80);
        Assert.Equal(0x80, a);
        Assert.Equal(0x00, b);
    }

    [Fact]
    public void ApplyRecolorsARenderedIconAndLeavesItsShadow()
    {
        // Two pixels: a white body and a #808080 shadow, as an icon render hands them over.
        var info = new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var source = new SKBitmap(info);
        source.SetPixel(0, 0, new SKColor(0xFF, 0xFF, 0xFF));
        source.SetPixel(1, 0, new SKColor(0x80, 0x80, 0x80));
        using var image = SKImage.FromBitmap(source);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        var selected = IconStatePainter.Apply(png, IconState.Selected, Colors);
        Assert.NotNull(selected);
        using var result = SKBitmap.Decode(selected);
        Assert.Equal(new SKColor(0xFF, 0xFF, 0x00), result.GetPixel(0, 0));
        Assert.Equal(new SKColor(0x80, 0x80, 0x80), result.GetPixel(1, 0));
    }

    [Fact]
    public void BytesThatAreNotAnImageAreRefusedRatherThanThrown()
    {
        Assert.Null(IconStatePainter.Apply([1, 2, 3], IconState.Hover, Colors));
    }
}
