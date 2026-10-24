using Wlrix.Settings.Background.Services;
using Xunit;

namespace Wlrix.Settings.Background.Tests;

/// <summary>
/// The wallpaper folders, against a fake XDG data path. In one collection because the data path
/// is process environment, which parallel tests would trample.
/// </summary>
[Collection("xdg")]
public sealed class WallpapersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wlrix-bg-panel-{Guid.NewGuid():N}");
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");

    public WallpapersTests()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Home);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", System);
    }

    private string Home => Path.Combine(_root, "home");
    private string System => Path.Combine(_root, "usr");
    private string Own => Path.Combine(Home, "wlrix", "wallpapers");
    private string Stock => Path.Combine(System, "wlrix", "wallpapers");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldHome);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", _oldDirs);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Touch(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public void TheUsersOwnComeFirstAndEachNameOnce()
    {
        Touch(Stock, "scatter.png");
        Touch(Stock, "cubes.rgb");
        Touch(Own, "scatter.png");
        Touch(Own, "holiday.jpg");

        var all = Wallpapers.All();

        Assert.Equal(
            [Path.Combine(Own, "holiday.jpg"), Path.Combine(Own, "scatter.png"), Path.Combine(Stock, "cubes.rgb")],
            all.Select(wallpaper => wallpaper.Path));
        Assert.Equal([true, true, false], all.Select(wallpaper => wallpaper.IsOwn));
    }

    [Fact]
    public void FilesTheDesktopCannotShowAreNotListed()
    {
        Touch(Stock, "scatter.png");
        Touch(Stock, "README");
        Touch(Stock, "notes.txt");
        Touch(Stock, "OLD.XPM");

        Assert.Equal(["OLD.XPM", "scatter.png"], Wallpapers.All().Select(wallpaper => Path.GetFileName(wallpaper.Path)));
    }

    [Fact]
    public void NoFoldersAtAllIsAnEmptyList() => Assert.Empty(Wallpapers.All());

    [Fact]
    public void AddingCopiesAndNeverReplaces()
    {
        var source = Touch(Path.Combine(_root, "Downloads"), "photo.jpg");

        var first = Wallpapers.Add(source);
        var second = Wallpapers.Add(source);
        var third = Wallpapers.Add(source);

        Assert.Equal(Path.Combine(Own, "photo.jpg"), first.Path);
        Assert.Equal(Path.Combine(Own, "photo (2).jpg"), second.Path);
        Assert.Equal(Path.Combine(Own, "photo (3).jpg"), third.Path);
        Assert.True(first.IsOwn);
        // A copy: the original is still where it was.
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void OnlyTheUsersOwnCanBeRemoved()
    {
        var stock = Touch(Stock, "scatter.png");
        var own = Wallpapers.Add(stock);

        Assert.Throws<InvalidOperationException>(() => Wallpapers.Remove(new Wallpaper(stock, IsOwn: false)));
        // Not even when something claims it is the user's: the path decides.
        Assert.Throws<InvalidOperationException>(() => Wallpapers.Remove(new Wallpaper(stock, IsOwn: true)));
        Assert.True(File.Exists(stock));

        Wallpapers.Remove(own);
        Assert.False(File.Exists(own.Path));
    }

    [Fact]
    public void AFileSkiaCannotReadHasNoThumbnail()
    {
        var xpm = Touch(Stock, "old.xpm");
        Assert.Null(Wallpapers.Thumbnail(xpm, 96));
        Assert.Null(Wallpapers.Thumbnail(Path.Combine(Stock, "absent.png"), 96));
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.rgb", true)]
    [InlineData("a.bw", true)]
    [InlineData("a.pcx", true)]
    [InlineData("a.svg", false)]
    [InlineData("a", false)]
    public void WhatCountsAsAPicture(string name, bool supported) =>
        Assert.Equal(supported, Wallpapers.IsSupported(name));
}
