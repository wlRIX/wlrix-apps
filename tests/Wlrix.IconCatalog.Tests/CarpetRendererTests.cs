// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using SkiaSharp;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Models;
using Wlrix.IconCatalog.Rendering;
using Wlrix.Icons;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

public class CarpetRendererTests
{
    // An empty theme: nothing an Icon= names resolves, so what is drawn is the carpet alone.
    private static readonly CarpetRenderer Renderer = new(new IconLoader(new XdgIconTheme([], []) { Theme = string.Empty }));

    private static CatalogIcon Launcher(string extra = "") => CatalogIcon.ForEntry(
        ItemRef.Application("a.desktop"),
        DesktopEntryParser.Parse("a.desktop", ("[Desktop Entry]\nType=Application\nName=A\nExec=a\nIcon=nothing-here\n" + extra).Split('\n'))!,
        CultureInfo.InvariantCulture);

    private static SKBitmap Decode(byte[]? png)
    {
        Assert.NotNull(png);
        return SKBitmap.Decode(png);
    }

    [Fact]
    public void ALauncherWithNoSymbolStillGetsItsCarpet()
    {
        using var bitmap = Decode(Renderer.Render(Launcher(), false, IconState.Normal, IconStateColors.Default, 64));
        Assert.Equal(64, bitmap.Width);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 0);
    }

    [Fact]
    public void TheCarpetStandsUpWhenTheApplicationIsRunning()
    {
        var closed = Renderer.Render(Launcher(), false, IconState.Hover, IconStateColors.Default, 64);
        var open = Renderer.Render(Launcher(), true, IconState.Hover, IconStateColors.Default, 64);
        Assert.NotEqual(closed, open);
    }

    [Fact]
    public void SelectionTintsTheCarpet()
    {
        using var hover = Decode(Renderer.Render(Launcher(), false, IconState.Hover, IconStateColors.Default, 64));
        using var selected = Decode(Renderer.Render(Launcher(), false, IconState.Selected, IconStateColors.Default, 64));

        // White under the pointer, yellow when selected: the carpet's body is the only change.
        Assert.Contains(hover.Pixels, pixel => pixel == SKColors.White);
        Assert.Contains(selected.Pixels, pixel => pixel == new SKColor(0xFF, 0xFF, 0x00));
        Assert.DoesNotContain(selected.Pixels, pixel => pixel == SKColors.White);
    }

    [Fact]
    public void AControlPanelFallsBackToTheGenericCarpetUntilItHasItsOwn()
    {
        var panel = Renderer.Render(Launcher("X-WLRIX-ControlPanel=true"), false, IconState.Normal, IconStateColors.Default, 64);
        var generic = Renderer.Render(Launcher(), false, IconState.Normal, IconStateColors.Default, 64);

        if (Renderer.HasArtwork(CarpetStyle.ControlPanel))
            Assert.NotEqual(generic, panel);
        else
            Assert.Equal(generic, panel);
    }

    [Fact]
    public void AFileWhoseIconDoesNotResolveDrawsNothing()
    {
        var file = new CatalogIcon { Item = ItemRef.File("/x"), Name = "x", IconNames = ["nothing-here"], Path = "/x" };
        Assert.Null(Renderer.Render(file, false, IconState.Normal, IconStateColors.Default, 64));
    }
}
