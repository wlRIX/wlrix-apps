using Avalonia;
using Wlrix.Desks.Models;
using Wlrix.Desks.ViewModels;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That a desk tile's preview says who its windows are.
/// </summary>
/// <remarks>
/// The projection used to drop both names on the floor, which is why the miniature windows had
/// nothing to put in a tooltip. Nothing else in the application would notice if it started
/// doing so again — the rectangles would still be in the right places.
/// </remarks>
public class DeskViewModelTests
{
    private static readonly Rect World = new(0, 0, 1920, 1080);

    [Fact]
    public void APreviewWindowKeepsItsTitleAndApplicationId()
    {
        var desk = new DeskViewModel(1);
        desk.Update(
            new DeskInfo(1, Active: true, "Web"),
            [new WindowInfo(101, 1, 100, 120, 760, 520, Minimized: false, "firefox", "Mozilla Firefox")],
            World, DeskScale.Default);

        var window = Assert.Single(desk.PreviewWindows);
        Assert.Equal(101, window.Id);
        Assert.Equal("Mozilla Firefox", window.Title);
        Assert.Equal("firefox", window.AppId);
    }

    [Fact]
    public void TheGeometryIsScaledIntoTheBox()
    {
        var desk = new DeskViewModel(1);
        desk.Update(
            new DeskInfo(1, Active: false, "Web"),
            [new WindowInfo(101, 1, 192, 108, 960, 540, Minimized: false, "term", "Terminal")],
            World, 10);

        // At 10% the box is the screen layout a tenth the size, so the windows reach all four
        // edges rather than letterboxing.
        const double scale = 0.1;
        var window = Assert.Single(desk.PreviewWindows);

        Assert.Equal(192 * scale, window.X, 6);
        Assert.Equal(108 * scale, window.Y, 6);
        Assert.Equal(960 * scale, window.W, 6);
        Assert.Equal(540 * scale, window.H, 6);
        Assert.Equal(192, desk.PreviewWidth);
        Assert.Equal(108, desk.PreviewHeight);
    }
}
