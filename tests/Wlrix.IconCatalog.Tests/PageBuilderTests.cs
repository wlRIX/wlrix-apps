// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Models;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

public class PageBuilderTests
{
    private static DesktopEntry Entry(string id, string name, string categories, string extra = "") =>
        DesktopEntryParser.Parse(id,
            $"""
            [Desktop Entry]
            Type=Application
            Name={name}
            Exec={id.Replace(".desktop", "")}
            Categories={categories}
            {extra}
            """.Split('\n'),
            "/usr/share/applications/" + id)!;

    private static IReadOnlyList<CatalogPage> Build(
        IReadOnlyList<DesktopEntry> installed, CatalogDocument? document = null,
        Func<string, CatalogIcon?>? resolveFile = null) =>
        PageBuilder.Build(installed, document ?? new CatalogDocument(), id => "page " + id,
            resolveFile ?? (_ => null), CultureInfo.InvariantCulture);

    private static IEnumerable<string> Names(CatalogPage page) => page.Icons.Select(icon => icon.Name);

    [Fact]
    public void AnApplicationAppearsOnEveryPageItsCategoriesName()
    {
        var pages = Build([Entry("steam.desktop", "Steam", "Network;FileTransfer;Game;")]);

        Assert.Equal(["Game", "Network"], pages.Select(page => page.Id));
        Assert.All(pages, page => Assert.Equal(["Steam"], Names(page)));
    }

    [Fact]
    public void PagesNothingBelongsToAreLeftOut()
    {
        var pages = Build([Entry("a.desktop", "A", "Office;")]);
        Assert.Equal(["Office"], pages.Select(page => page.Id));
    }

    [Fact]
    public void AnEntryWithNoMainCategoryGoesOnOther()
    {
        var pages = Build([Entry("a.desktop", "A", "X-Vendor;")]);
        Assert.Equal([MainCategory.Other], pages.Select(page => page.Id));
    }

    [Fact]
    public void AControlPanelGoesOnlyOnControlPanels()
    {
        var pages = Build([Entry("login.desktop", "Login", "Settings;System;", "X-WLRIX-ControlPanel=true")]);

        var page = Assert.Single(pages);
        Assert.Equal(SystemPages.ControlPanels, page.Id);
        Assert.True(page.Icons[0].IsControlPanel);
    }

    [Fact]
    public void AnyOtherSettingsToolGoesOnControlPanelsAndItsOtherPages()
    {
        var pages = Build([Entry("pavucontrol.desktop", "Volume", "AudioVideo;Audio;Settings;")]);

        Assert.Equal([SystemPages.ControlPanels, "AudioVideo"], pages.Select(page => page.Id));
        Assert.False(pages[0].Icons[0].IsControlPanel);
    }

    [Fact]
    public void ControlPanelsComesFirst()
    {
        var pages = Build([
            Entry("a.desktop", "A", "Utility;"),
            Entry("b.desktop", "B", "Settings;"),
            Entry("c.desktop", "C", "AudioVideo;"),
        ]);

        Assert.Equal([SystemPages.ControlPanels, "AudioVideo", "Utility"], pages.Select(page => page.Id));
    }

    [Fact]
    public void IconsAreSortedByLabelIgnoringCase()
    {
        var pages = Build([
            Entry("z.desktop", "zebra", "Utility;"),
            Entry("a.desktop", "Banana", "Utility;"),
            Entry("m.desktop", "apple", "Utility;"),
        ]);

        Assert.Equal(["apple", "Banana", "zebra"], Names(pages[0]));
    }

    [Fact]
    public void AHiddenApplicationIsOffThatPageOnly()
    {
        var document = new CatalogDocument();
        document.RemoveItem("Game", isSystem: true, "steam.desktop");

        var pages = Build([Entry("steam.desktop", "Steam", "Network;Game;")], document);

        Assert.Equal(["Network"], pages.Select(page => page.Id));
    }

    [Fact]
    public void AnIconDroppedOnASystemPageIsShownThere()
    {
        var document = new CatalogDocument();
        var installed = new[] { Entry("gimp.desktop", "GIMP", "Graphics;"), Entry("a.desktop", "A", "Office;") };
        document.AddItem("Office", isSystem: true, ItemRef.Application("gimp.desktop"), installedThere: false);

        var office = Build(installed, document).Single(page => page.Id == "Office");

        Assert.Equal(["A", "GIMP"], Names(office));
    }

    [Fact]
    public void CustomPagesFollowTheSystemPagesAndStayWhenEmpty()
    {
        var document = new CatalogDocument();
        document.AddPage("Mine");
        document.AddPage("Empty");
        document.AddItem(document.CustomPages[0].Id, isSystem: false, ItemRef.Application("a.desktop"), false);

        var pages = Build([Entry("a.desktop", "A", "Office;")], document);

        Assert.Equal(["Office", document.CustomPages[0].Id, document.CustomPages[1].Id], pages.Select(page => page.Id));
        Assert.Equal(["A"], Names(pages[1]));
        Assert.Empty(pages[2].Icons);
        Assert.Equal("Mine", pages[1].Name);
        Assert.False(pages[1].IsSystem);
    }

    [Fact]
    public void AnItemWhoseTargetIsGoneIsSkippedButKept()
    {
        var document = new CatalogDocument();
        var page = document.AddPage("Mine");
        document.AddItem(page.Id, isSystem: false, ItemRef.Application("gone.desktop"), false);
        document.AddItem(page.Id, isSystem: false, ItemRef.File("/gone/file"), false);

        var pages = Build([], document);

        Assert.Empty(pages.Single().Icons);
        Assert.Equal(2, document.CustomPages[0].Items.Count);
    }

    [Fact]
    public void DroppedFilesAreResolvedThroughTheCallback()
    {
        var document = new CatalogDocument();
        var page = document.AddPage("Mine");
        document.AddItem(page.Id, isSystem: false, ItemRef.File("/home/me/notes.txt"), false);

        var pages = Build([], document, path => new CatalogIcon { Item = ItemRef.File(path), Name = "notes.txt", Path = path });

        Assert.Equal(["notes.txt"], Names(pages.Single()));
    }

    [Fact]
    public void LabelsFollowTheDisplayLanguage()
    {
        var entry = Entry("a.desktop", "Clock", "Utility;", "Name[ja]=時計");
        var pages = PageBuilder.Build([entry], new CatalogDocument(), id => id, _ => null, new CultureInfo("ja-JP"));
        Assert.Equal(["時計"], Names(pages[0]));
    }
}
