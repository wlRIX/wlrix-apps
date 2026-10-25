// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Models;
using Xunit;

namespace Wlrix.IconCatalog.Tests;

public sealed class EntryEditingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wlrix-entry-edit-" + Guid.NewGuid().ToString("N"));
    private string User => Path.Combine(_root, "home", "applications");
    private string System => Path.Combine(_root, "usr", "applications");

    private const string Installed = """
        [Desktop Entry]
        Type=Application
        Name=Dolphin Emulator
        Name[ja]=Dolphin エミュレータ
        Exec=dolphin-emu %f
        DBusActivatable=true
        Categories=Game;

        [Desktop Action Wii]
        Exec=dolphin-emu -b
        """;

    public EntryEditingTests()
    {
        Directory.CreateDirectory(User);
        Directory.CreateDirectory(System);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string directory, string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text.Replace("\r", ""));
        return path;
    }

    private static CatalogIcon Icon(string path, ItemKind kind = ItemKind.Application)
    {
        var entry = DesktopEntryParser.Parse(Path.GetFileName(path), File.ReadLines(path), path)!;
        var item = kind == ItemKind.Application ? ItemRef.Application(entry.Id) : ItemRef.File(path);
        return CatalogIcon.ForEntry(item, entry, CultureInfo.InvariantCulture);
    }

    private EntryEditing For(CatalogIcon icon, CultureInfo? culture = null) =>
        EntryEditing.For(icon, User, id => File.Exists(Path.Combine(System, id)) ? Path.Combine(System, id) : null,
            culture ?? CultureInfo.InvariantCulture)!;

    [Fact]
    public void EditingAnInstalledEntryWritesAUserCopyAndLeavesTheInstalledOneAlone()
    {
        var installed = Write(System, "dolphin-emu.desktop", Installed);
        var editing = For(Icon(installed));
        Assert.True(editing.MakesCopy);
        Assert.False(editing.CanRevert);

        var fields = editing.Read();
        editing.Save(fields with { Command = "dolphin-emu -u ~/Games/Dolphin %f" });

        Assert.Equal(Installed.Replace("\r", ""), File.ReadAllText(installed));
        var copy = DesktopEntryParser.Parse("dolphin-emu.desktop", File.ReadLines(Path.Combine(User, "dolphin-emu.desktop")))!;
        Assert.Equal("dolphin-emu -u ~/Games/Dolphin %f", copy.Exec);
        // The copy keeps everything else, translations and actions included.
        Assert.Equal("Dolphin エミュレータ", copy.Name["ja"]);
        Assert.Contains("[Desktop Action Wii]", File.ReadAllText(Path.Combine(User, "dolphin-emu.desktop")));
    }

    [Fact]
    public void AnEditedCommandStopsDBusActivationFromBypassingIt()
    {
        var installed = Write(System, "dolphin-emu.desktop", Installed);
        var editing = For(Icon(installed));
        editing.Save(editing.Read() with { Command = "dolphin-emu -u" });
        Assert.DoesNotContain("DBusActivatable", File.ReadAllText(editing.TargetPath));
    }

    [Fact]
    public void EnvironmentGoesOnTheCommandAndComesBackOut()
    {
        var installed = Write(System, "dolphin-emu.desktop", Installed);
        For(Icon(installed)).Save(For(Icon(installed)).Read() with
        {
            Environment = [new("QT_QPA_PLATFORM", "xcb")],
        });

        var copy = Path.Combine(User, "dolphin-emu.desktop");
        var fields = For(Icon(copy)).Read();
        Assert.Equal([new("QT_QPA_PLATFORM", "xcb")], fields.Environment);
        Assert.Equal("dolphin-emu %f", fields.Command);
    }

    [Fact]
    public void TheUsersCopyIsEditedInPlaceAndCanBeReverted()
    {
        Write(System, "dolphin-emu.desktop", Installed);
        var copy = Write(User, "dolphin-emu.desktop", Installed.Replace("dolphin-emu %f", "dolphin-emu -u %f"));
        var editing = For(Icon(copy));

        Assert.False(editing.MakesCopy);
        Assert.True(editing.CanRevert);
        editing.Revert();
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public void AUsersOwnEntryWithNothingInstalledCannotBeReverted()
    {
        var own = Write(User, "mine.desktop", "[Desktop Entry]\nType=Application\nName=Mine\nExec=mine\n");
        var editing = For(Icon(own));
        Assert.False(editing.CanRevert);
        editing.Revert();
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void ALauncherDroppedFromTheDesktopIsEditedWhereItIsAndKeepsItsExecuteBit()
    {
        var desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(desktop);
        var launcher = Write(desktop, "dolphin-emu.desktop", Installed);
        File.SetUnixFileMode(launcher, File.GetUnixFileMode(launcher) | UnixFileMode.UserExecute);

        var editing = For(Icon(launcher, ItemKind.File));
        Assert.Equal(launcher, editing.TargetPath);
        editing.Save(editing.Read() with { Command = "dolphin-emu -u %f", Terminal = true });

        var saved = File.ReadAllText(launcher);
        Assert.Contains("Exec=dolphin-emu -u %f", saved);
        Assert.Contains("Terminal=true", saved);
        Assert.True((File.GetUnixFileMode(launcher) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    public void TheNameEditedIsTheOneTheUserSees()
    {
        var installed = Write(System, "dolphin-emu.desktop", Installed);
        var japanese = new CultureInfo("ja-JP");
        var editing = For(Icon(installed), japanese);
        Assert.Equal("Dolphin エミュレータ", editing.Read().Name);

        editing.Save(editing.Read() with { Name = "Dolphin（ポータブル）" });
        var saved = File.ReadAllText(editing.TargetPath);
        Assert.Contains("Name[ja]=Dolphin（ポータブル）", saved);
        Assert.Contains("Name=Dolphin Emulator", saved);
    }

    [Fact]
    public void NothingChangedChangesNoLine()
    {
        var installed = Write(System, "dolphin-emu.desktop", Installed);
        var editing = For(Icon(installed));
        editing.Save(editing.Read());
        // Line for line; the copy only ends with a newline, as a desktop entry should.
        Assert.Equal(File.ReadAllLines(installed), File.ReadAllLines(editing.TargetPath));
    }

    [Fact]
    public void AnIconWithoutALauncherCannotBeEdited()
    {
        var file = new CatalogIcon { Item = ItemRef.File("/x.txt"), Name = "x.txt", Path = "/x.txt" };
        Assert.Null(EntryEditing.For(file, User, _ => null, CultureInfo.InvariantCulture));
    }
}
