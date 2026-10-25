using Wlrix.Common.Desktop;
using Xunit;

namespace Wlrix.Common.Tests;

/// <summary>The preferred terminal, as xdg-terminal-exec stores it.</summary>
public class TerminalListTests
{
    [Fact]
    public void TheUsersFileBeatsTheSystemsAndTheDesktopsBeatsThePlainOne()
    {
        using var dir = new Scratch();
        dir.File("home/xdg-terminals.list", "foot.desktop\n");
        dir.File("home/wlrix-xdg-terminals.list", "kitty.desktop\n");
        dir.File("etc/xdg-terminals.list", "xterm.desktop\n");

        var list = new TerminalList(dir.Combine("home"), [dir.Combine("etc")], ["wlRIX"]);

        Assert.Equal("kitty.desktop", list.Preferred());
        Assert.Equal(dir.Combine("home/wlrix-xdg-terminals.list"), list.UserPath);
    }

    [Fact]
    public void ASystemListIsReadWhenTheUserHasNone()
    {
        using var dir = new Scratch();
        dir.File("etc/xdg-terminals.list", "# the site's choice\n+xterm.desktop:new-window\n");

        var list = new TerminalList(dir.Combine("home"), [dir.Combine("etc")], []);

        Assert.Equal("xterm.desktop", list.Preferred());
        Assert.Equal(dir.Combine("home/xdg-terminals.list"), list.UserPath);
    }

    [Fact]
    public void AnExcludedEntryIsNotPreferred() =>
        Assert.Equal(["foot.desktop"], TerminalList.Ids(["-kitty.desktop", "foot.desktop"]));

    [Fact]
    public void ChoosingMovesTheEntryToTheTopAndKeepsEverythingElse()
    {
        var lines = TerminalList.WithPreferred(["# mine", "foot.desktop", "+kitty.desktop:x", "xterm.desktop"], "kitty.desktop");

        Assert.Equal(["# mine", "kitty.desktop", "foot.desktop", "xterm.desktop"], lines);
    }

    [Fact]
    public void ChoosingWritesTheUsersFile()
    {
        using var dir = new Scratch();
        var list = new TerminalList(dir.Combine("home"), [], []);

        list.SetPreferred("foot.desktop");

        Assert.Equal("foot.desktop", list.Preferred());
        Assert.Empty(Directory.GetFiles(dir.Combine("home"), "*.wlrix-new"));
    }

    private static DesktopEntry Entry(string id, string exec, string? execArgument = null) => new()
    {
        Id = id,
        Name = new Dictionary<string, string> { [string.Empty] = id },
        Exec = exec,
        TerminalExecArgument = execArgument,
    };

    [Fact]
    public void TheChosenTerminalRunsWithItsOwnExecOption()
    {
        using var dir = new Scratch();
        dir.File("home/xdg-terminals.list", "gone.desktop\nshell.desktop\n");
        var list = new TerminalList(dir.Combine("home"), [], []);

        // /bin/sh stands in for a terminal: what matters is that it is on the machine.
        var terminal = TerminalEmulator.Resolve(list, () => [Entry("shell.desktop", "/bin/sh --login", "-c")]);

        Assert.NotNull(terminal);
        Assert.Equal("/bin/sh", terminal.File);
        Assert.Equal(["--login", "-c", "micro", "notes.txt"], terminal.Wrap("micro", ["notes.txt"]));
    }

    [Fact]
    public void AnEntryWithoutAnExecOptionGetsDashE() =>
        Assert.Equal("-e", TerminalEmulator.FromEntry(Entry("shell.desktop", "/bin/sh"))!.ExecArgument);

    [Fact]
    public void TheEntriesAreNotScannedWhenThereIsNoList()
    {
        using var dir = new Scratch();
        var scanned = false;

        TerminalEmulator.Resolve(new TerminalList(dir.Combine("home"), [], []), () =>
        {
            scanned = true;
            return [];
        });

        Assert.False(scanned);
    }
}
