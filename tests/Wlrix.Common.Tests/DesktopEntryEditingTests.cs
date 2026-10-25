using System.Globalization;
using Wlrix.Common.Desktop;
using Xunit;

namespace Wlrix.Common.Tests;

/// <summary>Editing a desktop entry: its environment, and the file around the changed keys.</summary>
public class DesktopEntryEditingTests
{
    // --- ExecEnvironment ------------------------------------------------------------------

    [Fact]
    public void ACommandWithoutEnvIsAllCommand()
    {
        var (variables, command) = ExecEnvironment.Split("dolphin-emu -u %f");
        Assert.Empty(variables);
        Assert.Equal("dolphin-emu -u %f", command);
    }

    [Fact]
    public void LeadingAssignmentsAreTheEnvironment()
    {
        var (variables, command) = ExecEnvironment.Split("""env QT_SCALE_FACTOR=2 MSG="hello there" dolphin-emu -u "My Dir" %f""");
        Assert.Equal([new("QT_SCALE_FACTOR", "2"), new("MSG", "hello there")], variables);
        // The command comes back as written, quotes and field codes included.
        Assert.Equal("""dolphin-emu -u "My Dir" %f""", command);
    }

    [Theory]
    [InlineData("env -u FOO dolphin-emu")]  // env's own options: someone's arrangement, left whole
    [InlineData("env dolphin-emu")]          // nothing set
    [InlineData("env FOO=1")]                // nothing run
    public void AnythingElseIsLeftWhole(string exec)
    {
        var (variables, command) = ExecEnvironment.Split(exec);
        Assert.Empty(variables);
        Assert.Equal(exec, command);
    }

    [Fact]
    public void JoiningQuotesWhatNeedsIt()
    {
        var exec = ExecEnvironment.Join(
            [new("SIMPLE", "/opt/x:1"), new("SPACED", "a b"), new("TRICKY", "$HOME \"q\"")],
            " dolphin-emu -u ");
        Assert.Equal("""env SIMPLE=/opt/x:1 SPACED="a b" TRICKY="\$HOME \"q\"" dolphin-emu -u""", exec);
        // And it reads back as it went in.
        var (variables, command) = ExecEnvironment.Split(exec);
        Assert.Equal([new("SIMPLE", "/opt/x:1"), new("SPACED", "a b"), new("TRICKY", "$HOME \"q\"")], variables);
        Assert.Equal("dolphin-emu -u", command);
    }

    [Fact]
    public void NoVariablesIsJustTheCommand() =>
        Assert.Equal("dolphin-emu", ExecEnvironment.Join([], "dolphin-emu"));

    [Theory]
    [InlineData("HOME", true)]
    [InlineData("_X1", true)]
    [InlineData("1X", false)]
    [InlineData("A-B", false)]
    [InlineData("", false)]
    public void NamesAreChecked(string name, bool valid) => Assert.Equal(valid, ExecEnvironment.IsValidName(name));

    // --- DesktopEntryEditor -----------------------------------------------------------------

    private static readonly string[] Dolphin =
    [
        "# Installed by the package",
        "[Desktop Entry]",
        "Type=Application",
        "Name=Dolphin Emulator",
        "Name[ja]=Dolphin エミュレータ",
        "Exec=dolphin-emu %f",
        "Terminal=false",
        "",
        "[Desktop Action Wii]",
        "Name=Wii Menu",
        "Exec=dolphin-emu -b",
    ];

    [Fact]
    public void AChangedKeyIsChangedWhereItIsAndNothingElseMoves()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetRaw("Exec", "dolphin-emu -u ~/Games/Dolphin %f");

        var expected = Dolphin.ToArray();
        expected[5] = "Exec=dolphin-emu -u ~/Games/Dolphin %f";
        Assert.Equal(expected, editor.Lines);
    }

    [Fact]
    public void ANewKeyGoesAtTheEndOfTheGroupNotIntoTheNextOne()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetRaw("Path", "/home/me/Games");

        Assert.Equal("Path=/home/me/Games", editor.Lines[7]);
        Assert.Equal("", editor.Lines[8]);
        Assert.Equal("[Desktop Action Wii]", editor.Lines[9]);
        // The action's own Exec is not the entry's.
        Assert.Equal("Exec=dolphin-emu -b", editor.Lines[^1]);
    }

    [Fact]
    public void AKeySetToNullIsRemoved()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetRaw("Terminal", null);
        Assert.DoesNotContain("Terminal=false", editor.Lines);
        Assert.Equal(Dolphin.Length - 1, editor.Lines.Count);
    }

    [Fact]
    public void ALocalizedNameIsTheOneChanged()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetString("Name", "Dolphin（ポータブル）", "ja");
        Assert.Contains("Name[ja]=Dolphin（ポータブル）", editor.Lines);
        Assert.Contains("Name=Dolphin Emulator", editor.Lines);
    }

    [Fact]
    public void StringsAreEscapedAndRawValuesAreNot()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetString("Comment", " back\\slash\nnew line");
        editor.SetRaw("Exec", """sh -c "echo \"hi\"" """.Trim());
        Assert.Contains(@"Comment=\sback\\slash\nnew line", editor.Lines);
        Assert.Contains("""Exec=sh -c "echo \"hi\"" """.Trim(), editor.Lines);
    }

    [Fact]
    public void WhatIsWrittenParsesBackToWhatWasSet()
    {
        var editor = new DesktopEntryEditor(Dolphin);
        editor.SetString("Name", "Portable Dolphin");
        editor.SetRaw("Exec", ExecEnvironment.Join([new("DOLPHIN_EMU_USERPATH", "/home/me/Games/Dolphin")], "dolphin-emu -u %f"));
        editor.SetRaw("Terminal", "true");

        var entry = DesktopEntryParser.Parse("dolphin-emu.desktop", editor.Lines)!;
        Assert.Equal("Portable Dolphin", DesktopEntryParser.ResolveLocalized(entry.Name, CultureInfo.InvariantCulture));
        Assert.Equal("env DOLPHIN_EMU_USERPATH=/home/me/Games/Dolphin dolphin-emu -u %f", entry.Exec);
        Assert.True(entry.Terminal);
        // The action group around the edit is still there, untouched.
        Assert.Contains("[Desktop Action Wii]", editor.Lines);
    }

    [Fact]
    public void TheResolvedLocaleIsTheOneShown()
    {
        var entry = DesktopEntryParser.Parse("d.desktop", Dolphin)!;
        Assert.Equal("ja", DesktopEntryParser.ResolvedLocale(entry.Name, new CultureInfo("ja-JP")));
        Assert.Equal("", DesktopEntryParser.ResolvedLocale(entry.Name, new CultureInfo("de-DE")));
    }
}
