using Wlrix.Common.Desktop;
using Wlrix.Files.Core.Mime;
using Wlrix.Files.Core.Platform;
using Wlrix.Settings.Utilities.Services;
using Xunit;

namespace Wlrix.Settings.Utilities.Tests;

/// <summary>What Apply writes, and what the panel reads back afterwards.</summary>
public class AssociationTests
{
    private static DesktopEntry App(string id, params string[] mimeTypes) => new()
    {
        Id = id,
        Name = new Dictionary<string, string> { [string.Empty] = id },
        Exec = "run %f",
        MimeType = mimeTypes,
    };

    private static MimeAppsList Empty()
    {
        using var dir = new Scratch();
        return MimeAppsList.Read(dir.Combine("none"));
    }

    [Fact]
    public void TheWholeOrderBecomesTheDefaultList()
    {
        var list = Empty();
        PendingChanges.Write(list, "text/plain", ["vim.desktop", "micro.desktop"], ["micro.desktop", "vim.desktop"]);

        Assert.Equal(["vim.desktop", "micro.desktop"], list.EntriesFor(MimeAppsList.DefaultApplications, "text/plain"));
        Assert.Empty(list.EntriesFor(MimeAppsList.AddedAssociations, "text/plain"));
        Assert.Empty(list.EntriesFor(MimeAppsList.RemovedAssociations, "text/plain"));
    }

    [Fact]
    public void AnApplicationThatDoesNotClaimTheTypeIsAdded()
    {
        var list = Empty();
        PendingChanges.Write(list, "text/plain", ["writer.desktop", "micro.desktop"], ["micro.desktop"]);

        Assert.Equal(["writer.desktop"], list.EntriesFor(MimeAppsList.AddedAssociations, "text/plain"));
    }

    [Fact]
    public void AnApplicationTakenOutOfTheOrderIsRemoved()
    {
        // Or it comes straight back, from its own MimeType=.
        var list = Empty();
        PendingChanges.Write(list, "text/plain", ["micro.desktop"], ["micro.desktop", "gimp.desktop"]);

        Assert.Equal(["gimp.desktop"], list.EntriesFor(MimeAppsList.RemovedAssociations, "text/plain"));
    }

    [Fact]
    public async Task ApplyWritesTheUsersFileAndTheOrderReadsBack()
    {
        using var dir = new Scratch();
        var user = dir.Combine("config/mimeapps.list");
        var system = dir.File("etc/mimeapps.list", """
            [Default Applications]
            text/plain=micro.desktop;
            """);
        var entries = new[] { App("micro.desktop", "text/plain"), App("vim.desktop", "text/plain"), App("gimp.desktop", "text/plain") };
        var associations = new Associations(
            entries,
            new ApplicationHandlers(entries, user, chainPaths: [user, system]),
            [dir.Combine("mime")],
            new UserMimeTypes(dir.Combine("data")),
            new TerminalList(dir.Combine("config"), [], []));

        Assert.Equal("micro.desktop", associations.OrderFor("text/plain")[0]);

        var pending = new PendingChanges();
        pending.SetOrder("text/plain", ["vim.desktop", "micro.desktop"]);
        pending.SetTerminal("foot.desktop");
        Assert.Null(await associations.ApplyAsync(pending));

        Assert.Equal(["vim.desktop", "micro.desktop"], associations.OrderFor("text/plain"));
        Assert.Equal(["gimp.desktop"], MimeAppsList.Read(user).EntriesFor(MimeAppsList.RemovedAssociations, "text/plain"));
        Assert.Equal("foot.desktop", associations.Terminals.Preferred());
    }

    [Fact]
    public void PendingChangesSayWhenThereAreNone()
    {
        var pending = new PendingChanges();
        var changes = 0;
        pending.Changed += () => changes++;

        Assert.True(pending.IsEmpty);
        pending.EditType(new UserMimeType("text/x-notes", ["*.notes"], null));
        Assert.False(pending.IsEmpty);
        Assert.True(pending.TouchesTypes);
        pending.Clear();

        Assert.True(pending.IsEmpty);
        Assert.Equal(2, changes);
    }
}
