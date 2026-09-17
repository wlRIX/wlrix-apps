using Wlrix.Files.Core;
using Wlrix.Files.Core.State;
using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>
/// Classic versus Modern. The whole difference is one policy object and one registry, and
/// this is where both are pinned — against a recording fake, so no display, no compositor and
/// no Avalonia are involved.
/// </summary>
public class WindowRoutingTests
{
    private static readonly Location Home = Location.Parse("/home/vic");
    private static readonly Location Downloads = Location.Parse("/home/vic/Downloads");
    private static readonly Location Music = Location.Parse("/home/vic/Music");

    /// <summary>A window is a name here. Nothing about routing needs it to be more.</summary>
    private sealed class FakeWindow(string name)
    {
        public override string ToString() => name;
    }

    private sealed class FakePresenter : IWindowPresenter<FakeWindow>
    {
        private int _created;

        public List<string> Calls { get; } = [];

        public FakeWindow Create(Location location)
        {
            var window = new FakeWindow($"w{++_created}");
            Calls.Add($"create {window} {location.Path}");
            return window;
        }

        public void Present(FakeWindow window) => Calls.Add($"present {window}");

        public void NavigateInPlace(FakeWindow window, Location location) =>
            Calls.Add($"navigate {window} {location.Path}");

        public void AddTab(FakeWindow window, Location location) =>
            Calls.Add($"tab {window} {location.Path}");
    }

    private static (WindowRouter<FakeWindow>, FakePresenter) Router(NavigationMode mode)
    {
        var presenter = new FakePresenter();
        return (new WindowRouter<FakeWindow>(presenter, () => mode), presenter);
    }

    // --- the gesture ------------------------------------------------------

    [Theory]
    [InlineData(false, false, false, OpenIntent.Default)]
    [InlineData(true, false, false, OpenIntent.NewTab)]
    [InlineData(false, true, false, OpenIntent.NewWindow)]
    [InlineData(false, false, true, OpenIntent.NewTab)]
    public void TheGestureIsReadTheSameWayInBothModes(
        bool control, bool shift, bool middle, OpenIntent expected) =>
        Assert.Equal(expected, NavigationPolicy.IntentFor(control, shift, middle));

    [Fact]
    public void AMiddleClickIsATabEvenWithShiftHeldByAccident() =>
        Assert.Equal(OpenIntent.NewTab, NavigationPolicy.IntentFor(control: false, shift: true, middleButton: true));

    [Fact]
    public void OnlyTheUnmodifiedCaseChangesWithTheMode()
    {
        Assert.Equal(OpenTarget.InPlace, NavigationPolicy.Decide(OpenIntent.Default, NavigationMode.Modern));
        Assert.Equal(OpenTarget.NewWindow, NavigationPolicy.Decide(OpenIntent.Default, NavigationMode.Classic));

        foreach (var mode in new[] { NavigationMode.Modern, NavigationMode.Classic })
        {
            Assert.Equal(OpenTarget.NewTab, NavigationPolicy.Decide(OpenIntent.NewTab, mode));
            Assert.Equal(OpenTarget.NewWindow, NavigationPolicy.Decide(OpenIntent.NewWindow, mode));
        }
    }

    // --- Modern -----------------------------------------------------------

    [Fact]
    public void ModernNavigatesTheWindowYouAreIn()
    {
        var (router, presenter) = Router(NavigationMode.Modern);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        router.Open(Downloads, OpenIntent.Default, window);

        Assert.Equal(["navigate w0 /home/vic/Downloads"], presenter.Calls);
        Assert.Equal(1, router.Registry.Count);
    }

    [Fact]
    public void ModernOpensASecondWindowOnADirectoryThatIsAlreadyShowing()
    {
        // Shift asked for a window in so many words. Answering by raising a different one
        // would be a refusal dressed up as a feature.
        var (router, presenter) = Router(NavigationMode.Modern);
        var first = new FakeWindow("w0");
        router.Track(first, Downloads);

        router.Open(Downloads, OpenIntent.NewWindow, first);

        Assert.Equal(["create w1 /home/vic/Downloads"], presenter.Calls);
        Assert.Equal(2, router.Registry.Count);
    }

    [Fact]
    public void ModernNavigatingOntoAnotherWindowsDirectoryJustGoesThere()
    {
        var (router, presenter) = Router(NavigationMode.Modern);
        var first = new FakeWindow("w0");
        var second = new FakeWindow("w1");
        router.Track(first, Downloads);
        router.Track(second, Home);

        router.Open(Downloads, OpenIntent.Default, second);

        Assert.Equal(["navigate w1 /home/vic/Downloads"], presenter.Calls);
        // The key stays with whoever had it, and the mover holds none rather than a stale one
        // still claiming it shows the directory it left.
        Assert.Equal(Downloads, router.Registry.KeyOf(first));
        Assert.Null(router.Registry.KeyOf(second));
    }

    // --- Classic ----------------------------------------------------------

    [Fact]
    public void ClassicOpensADirectoryAsItsOwnWindow()
    {
        var (router, presenter) = Router(NavigationMode.Classic);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        router.Open(Downloads, OpenIntent.Default, window);

        Assert.Equal(["create w1 /home/vic/Downloads"], presenter.Calls);
        Assert.Equal(2, router.Registry.Count);
    }

    [Fact]
    public void ClassicRaisesTheWindowThatAlreadyHasTheDirectory()
    {
        // The behavior the whole registry exists for, and the reason the Avalonia fork
        // needed xdg_activation_v1: without Activate() there is no way to raise a window.
        var (router, presenter) = Router(NavigationMode.Classic);
        var home = new FakeWindow("w0");
        router.Track(home, Home);
        router.Open(Downloads, OpenIntent.Default, home);
        presenter.Calls.Clear();

        router.Open(Downloads, OpenIntent.Default, home);

        Assert.Equal(["present w1"], presenter.Calls);
        Assert.Equal(2, router.Registry.Count);
    }

    [Fact]
    public void ClassicRaisesRatherThanNavigatingWhenATabWouldCollide()
    {
        // "Leave this one alone" cannot be done after the fact, so the claim is asked for
        // before the navigation rather than repaired afterwards.
        var (router, presenter) = Router(NavigationMode.Classic);
        var home = new FakeWindow("w0");
        var music = new FakeWindow("w1");
        router.Track(home, Home);
        router.Track(music, Music);

        var blocked = router.TryClaim(music, Home);

        Assert.Same(home, blocked);
        Assert.Equal(Music, router.Registry.KeyOf(music));
        Assert.Empty(presenter.Calls);
    }

    [Fact]
    public void ClassicKeepsTabsWorkingJustTheSame()
    {
        var (router, presenter) = Router(NavigationMode.Classic);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        router.Open(Downloads, OpenIntent.NewTab, window);

        Assert.Equal(["tab w0 /home/vic/Downloads"], presenter.Calls);
        Assert.Equal(1, router.Registry.Count);
    }

    // --- the registry -----------------------------------------------------

    [Fact]
    public void AWindowThatMovesTakesItsKeyWithIt()
    {
        var (router, _) = Router(NavigationMode.Classic);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        Assert.Null(router.TryClaim(window, Downloads));
        Assert.Equal(Downloads, router.Registry.KeyOf(window));
        Assert.False(router.Registry.TryGet(Home, out _));
        Assert.True(router.Registry.TryGet(Downloads, out var found));
        Assert.Same(window, found);
    }

    [Fact]
    public void ReclaimingTheKeyAWindowAlreadyHoldsIsFine()
    {
        // A reload navigates to where it already is, and it must not report itself blocked.
        var (router, _) = Router(NavigationMode.Classic);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        Assert.Null(router.TryClaim(window, Home));
        Assert.Equal(Home, router.Registry.KeyOf(window));
    }

    [Fact]
    public void ClosingAWindowFreesItsDirectoryForTheNextOne()
    {
        var (router, presenter) = Router(NavigationMode.Classic);
        var window = new FakeWindow("w0");
        router.Track(window, Downloads);

        router.Forget(window);

        Assert.Equal(0, router.Registry.Count);
        router.Open(Downloads, OpenIntent.Default, null);
        Assert.Equal(["create w1 /home/vic/Downloads"], presenter.Calls);
    }

    [Fact]
    public void AKeylessWindowTakesTheKeyBackWhenItIsFreedAgain()
    {
        var (router, _) = Router(NavigationMode.Modern);
        var first = new FakeWindow("w0");
        var second = new FakeWindow("w1");
        router.Track(first, Downloads);
        router.Track(second, Home);

        router.TryClaim(second, Downloads);
        Assert.Null(router.Registry.KeyOf(second));

        router.Forget(first);
        Assert.Null(router.TryClaim(second, Downloads));
        Assert.Equal(Downloads, router.Registry.KeyOf(second));
    }

    [Fact]
    public void OpeningWithNoWindowToOpenFromMakesOne()
    {
        // What startup does, and what a directory arriving from the desktop over D-Bus does.
        foreach (var mode in new[] { NavigationMode.Modern, NavigationMode.Classic })
        {
            var (router, presenter) = Router(mode);
            router.Open(Home, OpenIntent.Default, null);
            Assert.Equal(["create w1 /home/vic"], presenter.Calls);
        }
    }

    [Fact]
    public void ATabAskedForWithNoWindowBecomesAWindow()
    {
        var (router, presenter) = Router(NavigationMode.Modern);
        router.Open(Home, OpenIntent.NewTab, null);
        Assert.Equal(["create w1 /home/vic"], presenter.Calls);
    }

    [Fact]
    public void SwitchingModesMidSessionUsesTheNewOneImmediately()
    {
        // The mode is read through a delegate rather than captured, so the Options menu takes
        // effect on the next double-click instead of the next launch.
        var mode = NavigationMode.Modern;
        var presenter = new FakePresenter();
        var router = new WindowRouter<FakeWindow>(presenter, () => mode);
        var window = new FakeWindow("w0");
        router.Track(window, Home);

        router.Open(Downloads, OpenIntent.Default, window);
        Assert.Equal("navigate w0 /home/vic/Downloads", presenter.Calls[^1]);

        mode = NavigationMode.Classic;
        router.Open(Music, OpenIntent.Default, window);
        Assert.Equal("create w1 /home/vic/Music", presenter.Calls[^1]);
    }
}
