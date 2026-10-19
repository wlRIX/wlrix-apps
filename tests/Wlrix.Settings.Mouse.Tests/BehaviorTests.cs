// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Settings.Mouse.Converters;
using Wlrix.Settings.Mouse.Services;
using Wlrix.Settings.Mouse.ViewModels;
using Xunit;

namespace Wlrix.Settings.Mouse.Tests;

public class PresetTests
{
    [Fact]
    public void TheMiddleDiamondsAreTheDefaults()
    {
        // An untouched panel shows the middle of each row, as IRIX's did: libinput's own speed and
        // the compositor's own double-click time.
        Assert.Equal(2, Presets.IndexOf(Presets.Acceleration, 0.0));
        Assert.Equal(1, Presets.IndexOf(Presets.DoubleClick, 400));
    }

    [Fact]
    public void AValueBetweenPresetsLightsNoDiamond()
    {
        Assert.Equal(-1, Presets.IndexOf(Presets.Acceleration, 0.25));
        Assert.Equal(-1, Presets.IndexOf(Presets.DoubleClick, 410));
    }

    [Fact]
    public void AValueThatWentThroughTomlStillMatches() =>
        Assert.Equal(3, Presets.IndexOf(Presets.Acceleration, 0.5000000001));

    [Fact]
    public void SlowIsTheLongestDoubleClick() =>
        Assert.True(Presets.DoubleClick[0] > Presets.DoubleClick[^1]);

    [Fact]
    public void ADiamondIsCheckedByItsOwnIndexOnly()
    {
        var converter = PresetConverter.Instance;
        Assert.Equal(true, converter.Convert(2, typeof(bool), "2", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert(-1, typeof(bool), "2", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, converter.ConvertBack(true, typeof(int), "2", System.Globalization.CultureInfo.InvariantCulture));
        // Unchecking is another diamond's checking, which has already set the index.
        Assert.Same(global::Avalonia.Data.BindingOperations.DoNothing,
            converter.ConvertBack(false, typeof(int), "2", System.Globalization.CultureInfo.InvariantCulture));
    }
}

public class ClickSpeedSliderTests
{
    [Fact]
    public void SlowIsOnTheLeft()
    {
        // The slider reads Slow to Fast like the diamonds, so its left end is the longest time.
        Assert.Equal(100, ClickSpeedSlider.PositionOf(1500, 100, 1500));
        Assert.Equal(1500, ClickSpeedSlider.PositionOf(100, 100, 1500));
    }

    [Theory]
    [InlineData(250)]
    [InlineData(400)]
    [InlineData(600)]
    public void PositionAndTimeAreEachOthersInverse(int milliseconds) =>
        Assert.Equal(milliseconds,
            ClickSpeedSlider.TimeAt(ClickSpeedSlider.PositionOf(milliseconds, 100, 1500), 100, 1500));

    [Fact]
    public void TheTimeStaysInRange() =>
        Assert.Equal(100, ClickSpeedSlider.TimeAt(5000, 100, 1500));
}

public class ClickTestTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    private static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void TwoClicksInTimeAreADoubleClick()
    {
        var test = new ClickTest();
        Assert.Null(test.Press(At(0), Window));
        Assert.Equal(ClickKind.Double, test.Press(At(300), Window));
        // Settled: the timer running out afterwards has nothing to say.
        Assert.Null(test.Expire());
    }

    [Fact]
    public void AClickNobodyFollowsIsASingleClickOnceTheTimeIsUp()
    {
        var test = new ClickTest();
        Assert.Null(test.Press(At(0), Window));
        Assert.True(test.Pending);
        Assert.Equal(ClickKind.Single, test.Expire());
        Assert.False(test.Pending);
    }

    [Fact]
    public void ASecondClickTooLateStartsOver()
    {
        var test = new ClickTest();
        Assert.Null(test.Press(At(0), Window));
        Assert.Null(test.Press(At(500), Window));
        Assert.Equal(ClickKind.Double, test.Press(At(700), Window));
    }

    [Fact]
    public void ThreeQuickClicksAreADoubleAndThenAFirst()
    {
        var test = new ClickTest();
        test.Press(At(0), Window);
        Assert.Equal(ClickKind.Double, test.Press(At(100), Window));
        Assert.Null(test.Press(At(200), Window));
        Assert.True(test.Pending);
    }
}

public class CursorThemesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wlrix-mouse-tests").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// An XCursor file holding one image per entry of <paramref name="sizes"/>, each 1x1 and
    /// filled with its own size as the pixel, so a test can tell which one was picked.
    /// </summary>
    private static byte[] CursorFile(params int[] sizes)
    {
        const uint ImageType = 0xfffd0002;
        var file = new List<byte>();
        void Word(uint value) => file.AddRange(BitConverter.GetBytes(value));

        Word(BitConverter.ToUInt32("Xcur"u8));
        Word(16);
        Word(1);
        Word((uint)sizes.Length);
        var chunkAt = 16 + sizes.Length * 12;
        foreach (var size in sizes)
        {
            Word(ImageType);
            Word((uint)size);
            Word((uint)chunkAt);
            chunkAt += 36 + 4;
        }
        foreach (var size in sizes)
        {
            Word(36);
            Word(ImageType);
            Word((uint)size);
            Word(1);
            Word(1); // width
            Word(1); // height
            Word(3); // hotspot x
            Word(4); // hotspot y
            Word(0); // delay
            Word((uint)size);
        }
        return file.ToArray();
    }

    private void Theme(string root, string name, string? display, params string[] cursors)
    {
        var dir = Path.Combine(_root, root, name);
        Directory.CreateDirectory(dir);
        if (display is not null)
            File.WriteAllText(Path.Combine(dir, "index.theme"), $"[Icon Theme]\nName={display}\nInherits=Adwaita\n");
        if (cursors.Length == 0)
            return;
        Directory.CreateDirectory(Path.Combine(dir, "cursors"));
        foreach (var cursor in cursors)
            File.WriteAllBytes(Path.Combine(dir, "cursors", cursor), CursorFile(24, 32, 48));
    }

    [Fact]
    public void TheNearestSizeIsRead()
    {
        var image = CursorThemes.Read(CursorFile(24, 32, 48), 30);
        Assert.NotNull(image);
        Assert.Equal((1, 1, 3, 4), (image.Width, image.Height, image.XHot, image.YHot));
        // The pixel is the size it was filed under, little-endian: 32.
        Assert.Equal(new byte[] { 32, 0, 0, 0 }, image.Bgra);
    }

    [Fact]
    public void SomethingElseIsNotACursor()
    {
        Assert.Null(CursorThemes.Read("not a cursor at all"u8, 32));
        // Truncated mid-table.
        Assert.Null(CursorThemes.Read(CursorFile(32).AsSpan(0, 20), 32));
    }

    [Fact]
    public void OnlyThemesWithCursorsAreListed()
    {
        Theme("a", "sgi", "SGI", "left_ptr");
        // An icon theme by the same name as a cursor theme would be, and an inherit-only one.
        Theme("a", "wlrix", "wlRIX Icons");
        Theme("a", "default", null);

        var themes = CursorThemes.List([Path.Combine(_root, "a")]);
        Assert.Equal(["sgi"], themes.Select(theme => theme.Name));
        Assert.Equal("SGI", themes[0].Display);
    }

    [Fact]
    public void TheEarlierDirectoryWins()
    {
        Theme("user", "sgi", "Mine", "left_ptr");
        Theme("system", "sgi", "Theirs", "left_ptr");
        Theme("system", "Adwaita", null, "default");

        var search = new[] { Path.Combine(_root, "user"), Path.Combine(_root, "system") };
        var themes = CursorThemes.List(search);
        Assert.Equal(["Adwaita", "Mine"], themes.Select(theme => theme.Display));
    }

    [Fact]
    public void AShapeIsFoundByItsLegacyName()
    {
        Theme("a", "sgi", null, "left_ptr", "xterm");
        var search = new[] { Path.Combine(_root, "a") };
        Assert.EndsWith("left_ptr", CursorThemes.Find(search, "sgi", "default", "left_ptr"));
        Assert.Null(CursorThemes.Find(search, "sgi", "wait", "watch"));
        Assert.Null(CursorThemes.Find(search, "nonesuch", "default"));
    }
}

public class TouchpadTests
{
    [Fact]
    public void TappingIsOnByDefault()
    {
        // The compositor's default, which the tab has to show for an untouched file: a pad with
        // no buttons is unusable without it.
        Assert.True(TouchpadValues.Defaults.Tap);
        Assert.True(TouchpadValues.Defaults.DisableWhileTyping);
        Assert.Null(TouchpadValues.Defaults.Scroll);
        Assert.Null(TouchpadValues.Defaults.SecondaryClick);
    }

    [Fact]
    public void AUdevRecordNamesATouchpadByItsTag()
    {
        Assert.True(Touchpads.IsTouchpad("S:input/by-path/x\nE:ID_INPUT=1\nE:ID_INPUT_TOUCHPAD=1\nG:seat\n"));
        Assert.False(Touchpads.IsTouchpad("E:ID_INPUT=1\nE:ID_INPUT_MOUSE=1\n"));
        Assert.False(Touchpads.IsTouchpad("E:ID_INPUT_TOUCHPAD_INTEGRATION=internal\n"));
    }

    [Fact]
    public void OnlyInputDeviceRecordsAreRead()
    {
        var directory = Directory.CreateTempSubdirectory("wlrix-mouse-udev-");
        try
        {
            Assert.False(Touchpads.Present(directory.FullName));
            // A block device's record with the tag is not an input device.
            File.WriteAllText(Path.Combine(directory.FullName, "b8:0"), "E:ID_INPUT_TOUCHPAD=1\n");
            File.WriteAllText(Path.Combine(directory.FullName, "c13:64"), "E:ID_INPUT_MOUSE=1\n");
            Assert.False(Touchpads.Present(directory.FullName));
            File.WriteAllText(Path.Combine(directory.FullName, "c13:65"), "E:ID_INPUT_TOUCHPAD=1\n");
            Assert.True(Touchpads.Present(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
        Assert.False(Touchpads.Present(directory.FullName), "no directory, no touchpad");
    }
}
