// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Models;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

// The cases are wlrix-desktop's, from running.rs, so the two agree on which carpets stand.
public class RunningIdentitiesTests
{
    private static DesktopEntry Entry(string id, string body) =>
        DesktopEntryParser.Parse(id, ("[Desktop Entry]\nType=Application\nName=X\n" + body).Split('\n'),
            "/usr/share/applications/" + id)!;

    [Fact]
    public void StartupWmClassComesFirstThenTheFileThenTheProgram()
    {
        var entry = Entry("org.wezfurlong.wezterm.desktop", "StartupWMClass=WezTerm\nExec=/usr/bin/wezterm start");
        Assert.Equal(["wezterm", "org.wezfurlong.wezterm"], RunningIdentities.For(entry));
    }

    [Fact]
    public void NamesAreLowercasedAndNotRepeated()
    {
        var entry = Entry("Alacritty.desktop", "Exec=alacritty");
        Assert.Equal(["alacritty"], RunningIdentities.For(entry));
    }

    [Fact]
    public void AQuotedProgramPathIsUnquoted()
    {
        var entry = Entry("foo.desktop", "Exec=\"/opt/My App/bin/myapp\" %U");
        Assert.Equal(["foo", "myapp"], RunningIdentities.For(entry));
    }

    [Fact]
    public void AnyOpenWindowCounts()
    {
        var identities = RunningIdentities.For(Entry("mpv.desktop", "Exec=mpv"));
        Assert.True(RunningIdentities.AnyOpen(identities, new HashSet<string> { "firefox", "mpv" }));
        Assert.False(RunningIdentities.AnyOpen(identities, new HashSet<string> { "firefox" }));
    }
}
