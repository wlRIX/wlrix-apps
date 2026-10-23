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

    /// <summary>A one-directory theme holding <paramref name="icons"/>, by name.</summary>
    private static string Theme(params (string Name, string Svg)[] icons)
    {
        var root = Path.Combine(Path.GetTempPath(), "wlrix-carpet-" + Guid.NewGuid().ToString("N"));
        var apps = Path.Combine(root, "hicolor", "scalable", "apps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(root, "hicolor", "index.theme"),
            "[Icon Theme]\nName=hicolor\nDirectories=scalable/apps\n\n[scalable/apps]\nSize=48\nMinSize=1\nMaxSize=512\nType=Scalable\n");
        foreach (var (name, svg) in icons)
            File.WriteAllText(Path.Combine(apps, name + ".svg"), svg);
        return root;
    }

    private static CatalogIcon Named(string icon) => CatalogIcon.ForEntry(
        ItemRef.Application("a.desktop"),
        DesktopEntryParser.Parse("a.desktop", $"[Desktop Entry]\nType=Application\nName=A\nExec=a\nIcon={icon}".Split('\n'))!,
        CultureInfo.InvariantCulture);

    [Fact]
    public void AnIrixSymbolIsLaidOverTheCarpetInRegisterAndAnOrdinaryIconIsShrunk()
    {
        // Each draws one black square in its frame's top-left corner, where no carpet reaches.
        // Laid over 1:1, the IRIX one is still there; shrunk and stood on the carpet, the other
        // has moved in from the corner. And the IRIX one is without a namespace, as exported.
        var root = Theme(
            ("irix", """<svg width="50pt" height="50pt" viewBox="0 0 50 50"><g transform="scale(0.5)"><rect x="0" y="0" width="12" height="12" fill="#000"/></g></svg>"""),
            ("plain", """<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><rect x="0" y="0" width="6" height="6" fill="#000"/></svg>"""));
        try
        {
            var renderer = new CarpetRenderer(new IconLoader(new XdgIconTheme([root], []) { Theme = "hicolor" }));
            using var irix = Decode(renderer.Render(Named("irix"), false, IconState.Normal, IconStateColors.Default, 64));
            using var plain = Decode(renderer.Render(Named("plain"), false, IconState.Normal, IconStateColors.Default, 64));

            Assert.Equal(SKColors.Black, irix.GetPixel(2, 2));
            Assert.Equal(0, plain.GetPixel(2, 2).Alpha);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnIrixSymbolIsTintedWithItsCarpetAndAnOrdinaryIconIsNot()
    {
        // A white square in each: the body the tint acts on. Selected, the IRIX one turns
        // yellow as the IRIX icon catalog drew it; the ordinary one keeps its own white.
        var root = Theme(
            ("irix", """<svg width="50pt" height="50pt" viewBox="0 0 50 50"><g transform="scale(0.5)"><rect x="0" y="0" width="12" height="12" fill="#fff"/></g></svg>"""),
            ("plain", """<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><rect x="18" y="0" width="12" height="12" fill="#fff"/></svg>"""));
        try
        {
            var renderer = new CarpetRenderer(new IconLoader(new XdgIconTheme([root], []) { Theme = "hicolor" }));
            using var irix = Decode(renderer.Render(Named("irix"), false, IconState.Selected, IconStateColors.Default, 64));
            using var plain = Decode(renderer.Render(Named("plain"), false, IconState.Selected, IconStateColors.Default, 64));

            Assert.Equal(new SKColor(0xFF, 0xFF, 0x00), irix.GetPixel(2, 2));
            // The plain square, shrunk and stood on the carpet, is at the top center.
            Assert.Equal(SKColors.White, plain.GetPixel(32, 8));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFileWhoseIconDoesNotResolveDrawsNothing()
    {
        var file = new CatalogIcon { Item = ItemRef.File("/x"), Name = "x", IconNames = ["nothing-here"], Path = "/x" };
        Assert.Null(Renderer.Render(file, false, IconState.Normal, IconStateColors.Default, 64));
    }
}
