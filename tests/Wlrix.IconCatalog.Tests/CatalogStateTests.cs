// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.IconCatalog.Models;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

public sealed class CatalogStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wlrix-icon-catalog-" + Guid.NewGuid().ToString("N"));

    private string StateFile => Path.Combine(_directory, "state.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void WhatIsWrittenReadsBack()
    {
        var state = new CatalogState(StateFile);
        state.Change(document =>
        {
            var page = document.AddPage("Tools");
            document.AddItem(page.Id, false, ItemRef.File("/usr/bin/htop"), false);
            document.AddItem("Game", true, ItemRef.Application("steam.desktop"), false);
            document.RemoveItem("Network", true, "steam.desktop");
            document.LastPage = page.Id;
            document.WindowWidth = 640;
        });

        var again = new CatalogState(StateFile).Document;

        var tools = Assert.Single(again.CustomPages);
        Assert.Equal("Tools", tools.Name);
        Assert.Equal([ItemRef.File("/usr/bin/htop")], tools.Items);
        Assert.Equal([ItemRef.Application("steam.desktop")], again.SystemPages["Game"].Added);
        Assert.Equal(["steam.desktop"], again.SystemPages["Network"].Hidden);
        Assert.Equal(tools.Id, again.LastPage);
        Assert.Equal(640, again.WindowWidth);
    }

    [Fact]
    public void AMissingFileIsTheDefaults()
    {
        var document = new CatalogState(StateFile).Document;
        Assert.Empty(document.CustomPages);
        Assert.Empty(document.SystemPages);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 99, "customPages": [ { "name": "Future" } ] }""")]
    public void AnUnreadableFileIsTheDefaults(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StateFile, contents);

        var warnings = new List<string>();
        var document = new CatalogState(StateFile, warnings.Add).Document;

        Assert.Empty(document.CustomPages);
    }

    [Fact]
    public void NullListsInAHandEditedFileAreTreatedAsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StateFile,
            """{ "version": 1, "customPages": [ { "id": "x", "name": "A", "items": null } ], "systemPages": { "Game": { "added": null, "hidden": null }, "Office": null } }""");

        var document = new CatalogState(StateFile).Document;

        Assert.Empty(document.CustomPages[0].Items);
        Assert.Empty(document.SystemPages["Game"].Added);
        Assert.False(document.SystemPages.ContainsKey("Office"));
        // And the edit paths work on what was read.
        document.RemoveItem("Office", true, "a.desktop");
        Assert.Equal(["a.desktop"], document.SystemPages["Office"].Hidden);
    }

    [Fact]
    public void DroppingAHiddenApplicationBackUnhidesIt()
    {
        var document = new CatalogDocument();
        document.RemoveItem("Game", true, "steam.desktop");

        Assert.True(document.AddItem("Game", true, ItemRef.Application("steam.desktop"), installedThere: true));
        Assert.False(document.SystemPages.ContainsKey("Game"));
    }

    [Fact]
    public void DroppingSomethingAlreadyThereChangesNothing()
    {
        var document = new CatalogDocument();
        var page = document.AddPage("Mine");

        Assert.True(document.AddItem(page.Id, false, ItemRef.File("/a"), false));
        Assert.False(document.AddItem(page.Id, false, ItemRef.File("/a"), false));
        Assert.False(document.AddItem("Game", true, ItemRef.Application("steam.desktop"), installedThere: true));
        Assert.Single(page.Items);
        Assert.False(document.SystemPages.ContainsKey("Game"));
    }

    [Fact]
    public void RemovingAnAddedIconFromASystemPageForgetsItRatherThanHidingIt()
    {
        var document = new CatalogDocument();
        document.AddItem("Office", true, ItemRef.File("/a"), false);
        document.RemoveItem("Office", true, "/a");

        Assert.False(document.SystemPages.ContainsKey("Office"));
    }
}
