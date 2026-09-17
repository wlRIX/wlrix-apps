using System.Globalization;
using Wlrix.Common.Desktop;
using Xunit;

namespace Wlrix.Common.Tests;

public class DesktopEntryParserTests
{
    private static DesktopEntry Parse(string content, string id = "test.desktop") =>
        DesktopEntryParser.Parse(id, content.Split('\n'))
        ?? throw new InvalidOperationException("expected the entry to parse");

    [Fact]
    public void TheKeysAFileManagerNeedsAreRead()
    {
        // Icon, MimeType and StartupWMClass were all absent while only the Toolchest
        // used this: a menu draws its own glyphs and never asks what opens a file.
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=Archiver
            Exec=wlrix-archiver %f
            Icon=wlrix-archiver
            MimeType=application/zip;application/x-tar;
            StartupWMClass=com.wlrix.archiver
            Path=/var/tmp
            Keywords=archive;compress;
            """);

        Assert.Equal("wlrix-archiver", entry.Icon);
        Assert.Equal(["application/zip", "application/x-tar"], entry.MimeType);
        Assert.Equal("com.wlrix.archiver", entry.StartupWmClass);
        Assert.Equal("/var/tmp", entry.Path);
        Assert.Equal("archive;compress;", entry.Keywords[string.Empty]);
    }

    [Fact]
    public void ActionGroupsAreReadInTheOrderActionsDeclaresThem()
    {
        // Not the order the groups appear in: Actions= is the application saying how
        // it wants them presented.
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=Browser
            Exec=browser
            Actions=NewWindow;NewPrivate;

            [Desktop Action NewPrivate]
            Name=New Private Window
            Exec=browser --private

            [Desktop Action NewWindow]
            Name=New Window
            Exec=browser --new-window
            Icon=window-new
            """);

        Assert.Equal(["NewWindow", "NewPrivate"], entry.Actions.Select(a => a.Id));
        Assert.Equal("New Window", entry.Actions[0].Name[string.Empty]);
        Assert.Equal("browser --new-window", entry.Actions[0].Exec);
        Assert.Equal("window-new", entry.Actions[0].Icon);
        Assert.Null(entry.Actions[1].Icon);
    }

    [Fact]
    public void AnActionGroupWithNoEntryInActionsIsIgnored()
    {
        // The spec says Actions= is the list; a stray group is not an offered verb.
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=App
            Exec=app

            [Desktop Action Orphan]
            Name=Orphan
            Exec=app --orphan
            """);
        Assert.Empty(entry.Actions);
    }

    [Fact]
    public void AnActionNamedInActionsWithNoGroupIsSkippedRatherThanFabricated()
    {
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=App
            Exec=app
            Actions=Missing;
            """);
        Assert.Empty(entry.Actions);
    }

    [Fact]
    public void KeysAfterTheMainGroupNoLongerLeakIntoIt()
    {
        // The old parser stopped at the first group boundary. Now that it walks the
        // whole file, a key in another group must not be picked up as the entry's.
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=App
            Exec=app

            [Desktop Action Other]
            Name=Other
            Exec=app --other
            Icon=should-not-be-the-entry-icon
            """);
        Assert.Null(entry.Icon);
        Assert.Equal("app", entry.Exec);
    }

    [Fact]
    public void AnUnknownGroupIsIgnoredEntirely()
    {
        var entry = Parse("""
            [Desktop Entry]
            Type=Application
            Name=App
            Exec=app

            [X-Vendor-Extension]
            Exec=something-else
            Icon=nope
            """);
        Assert.Equal("app", entry.Exec);
        Assert.Null(entry.Icon);
    }

    [Fact]
    public void TheFirstOccurrenceOfADuplicateKeyWins()
    {
        var entry = Parse("""
            [Desktop Entry]
            Name=App
            Exec=first
            Exec=second
            """);
        Assert.Equal("first", entry.Exec);
    }

    [Fact]
    public void LocalizedNamesAreKeptPerLocaleAndResolvedWithTheSpecsFallback()
    {
        var entry = Parse("""
            [Desktop Entry]
            Name=Files
            Name[ja]=ファイル
            Name[pt_BR]=Arquivos
            Exec=files
            """);

        Assert.Equal("ファイル", DesktopEntryParser.ResolveLocalized(entry.Name, new CultureInfo("ja-JP")));
        Assert.Equal("Arquivos", DesktopEntryParser.ResolveLocalized(entry.Name, new CultureInfo("pt-BR")));
        // pt-PT has no entry, so it falls back past pt to the unlocalized value.
        Assert.Equal("Files", DesktopEntryParser.ResolveLocalized(entry.Name, new CultureInfo("pt-PT")));
        Assert.Equal("Files", DesktopEntryParser.ResolveLocalized(entry.Name, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnEntryWithNoNameIsRejected()
    {
        Assert.Null(DesktopEntryParser.Parse("x", ["[Desktop Entry]", "Exec=app"]));
    }

    [Fact]
    public void KeysBeforeAnyGroupHeaderAreIgnored()
    {
        // Malformed, but such files exist, and reading them would attribute another
        // program's Exec to this entry.
        var entry = DesktopEntryParser.Parse("x", ["Exec=stray", "[Desktop Entry]", "Name=App", "Exec=real"]);
        Assert.Equal("real", entry!.Exec);
    }

    [Fact]
    public void EscapeSequencesInValuesAreUnescaped()
    {
        var entry = Parse("""
            [Desktop Entry]
            Name=A\sName\nwith\tescapes
            Exec=app
            """);
        Assert.Equal("A Name\nwith\tescapes", entry.Name[string.Empty]);
    }

    [Fact]
    public void TheFilePathIsCarriedThroughForTheEntryPathFieldCode()
    {
        var entry = DesktopEntryParser.Parse("x", ["[Desktop Entry]", "Name=App", "Exec=app"], "/usr/share/applications/x.desktop");
        Assert.Equal("/usr/share/applications/x.desktop", entry!.FilePath);
    }
}
