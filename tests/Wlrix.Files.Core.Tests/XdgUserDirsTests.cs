using Wlrix.Files.Core.Platform;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class XdgUserDirsTests
{
    private const string Home = "/home/vic";

    [Fact]
    public void HomeAnchoredEntriesAreExpanded()
    {
        var dirs = XdgUserDirs.Parse(Home, [
            "# This file is written by xdg-user-dirs-update",
            "XDG_DESKTOP_DIR=\"$HOME/Desktop\"",
            "XDG_DOWNLOAD_DIR=\"$HOME/Downloads\""
        ], _ => null);

        Assert.Equal("/home/vic/Desktop", dirs.Get("XDG_DESKTOP_DIR")!.Path);
        Assert.Equal("/home/vic/Downloads", dirs.Get("XDG_DOWNLOAD_DIR")!.Path);
    }

    [Fact]
    public void ALocalizedFolderNameIsHonoredRatherThanAssumed()
    {
        // The whole reason the file exists: a Japanese session has ダウンロード, not
        // Downloads, and guessing the English name would point at nothing.
        var dirs = XdgUserDirs.Parse(Home, ["XDG_DOWNLOAD_DIR=\"$HOME/ダウンロード\""], _ => null);
        Assert.Equal("/home/vic/ダウンロード", dirs.Get("XDG_DOWNLOAD_DIR")!.Path);
    }

    [Fact]
    public void AnAbsolutePathOutsideHomeIsAccepted()
    {
        var dirs = XdgUserDirs.Parse(Home, ["XDG_MUSIC_DIR=\"/mnt/media/music\""], _ => null);
        Assert.Equal("/mnt/media/music", dirs.Get("XDG_MUSIC_DIR")!.Path);
    }

    [Fact]
    public void TheEnvironmentBeatsTheFile()
    {
        var dirs = XdgUserDirs.Parse(Home, ["XDG_DESKTOP_DIR=\"$HOME/Desktop\""],
            key => key == "XDG_DESKTOP_DIR" ? "/tmp/override" : null);
        Assert.Equal("/tmp/override", dirs.Get("XDG_DESKTOP_DIR")!.Path);
    }

    [Fact]
    public void AKeyNotInTheFileFallsBackToTheConventionalName()
    {
        var dirs = XdgUserDirs.Parse(Home, [], _ => null);
        Assert.Equal("/home/vic/Pictures", dirs.Get("XDG_PICTURES_DIR")!.Path);
    }

    [Fact]
    public void CommentsBlankLinesAndJunkAreIgnored()
    {
        var dirs = XdgUserDirs.Parse(Home, [
            "",
            "# a comment",
            "not an assignment",
            "SOMETHING_ELSE=\"/x\"",
            "XDG_MUSIC_DIR=\"$HOME/Music\""
        ], _ => null);
        Assert.Equal("/home/vic/Music", dirs.Get("XDG_MUSIC_DIR")!.Path);
        Assert.Null(dirs.Get("SOMETHING_ELSE"));
    }

    [Fact]
    public void AKeyPointingAtHomeIsOmittedFromPlaces()
    {
        // The spec's way of saying "this user has no such folder". Showing Home twice
        // under two names would be worse than leaving it out.
        var dirs = XdgUserDirs.Parse(Home, ["XDG_DESKTOP_DIR=\"$HOME\""], _ => null);
        var places = dirs.Places(_ => true);
        Assert.DoesNotContain(places, p => p.Key == "XDG_DESKTOP_DIR");
    }

    [Fact]
    public void PlacesSkipsDirectoriesThatDoNotExist()
    {
        // user-dirs.dirs is written once at first login; the folders can be deleted
        // afterwards, and a sidebar entry that goes nowhere is worse than none.
        var dirs = XdgUserDirs.Parse(Home, [], _ => null);
        var places = dirs.Places(path => path.EndsWith("Documents", StringComparison.Ordinal));
        Assert.Single(places);
        Assert.Equal("XDG_DOCUMENTS_DIR", places[0].Key);
    }

    [Fact]
    public void PlacesComeBackInSidebarOrderNotFileOrder()
    {
        var dirs = XdgUserDirs.Parse(Home, [
            "XDG_VIDEOS_DIR=\"$HOME/Videos\"",
            "XDG_DESKTOP_DIR=\"$HOME/Desktop\""
        ], _ => null);
        var keys = dirs.Places(_ => true).Select(p => p.Key).ToList();
        Assert.Equal(XdgUserDirs.PlacesKeys.ToList(), keys);
    }

    [Fact]
    public void UnquotedValuesAlsoParse()
    {
        var dirs = XdgUserDirs.Parse(Home, ["XDG_DESKTOP_DIR=$HOME/Desktop"], _ => null);
        Assert.Equal("/home/vic/Desktop", dirs.Get("XDG_DESKTOP_DIR")!.Path);
    }
}
