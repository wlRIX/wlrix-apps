using Wlrix.Settings.Displays.NightLight;
using Wlrix.Settings.Displays.ViewModels;
using Wlrix.Settings.Outputs;
using Xunit;

namespace Wlrix.Settings.Displays.Tests;

public class NightLightViewModelTests
{
    /// <summary>
    /// A page over an in-memory store, with the dispatcher and the save timer replaced: changes
    /// from elsewhere run in place, and a save waits in <c>Pending</c> until a test runs it.
    /// </summary>
    private sealed class Page
    {
        public Page(SampleNightLightStore? store = null)
        {
            Store = store ?? new SampleNightLightStore();
            Model = new NightLightViewModel(Store, action => action(), Pending.Add);
            Model.InitializeAsync().GetAwaiter().GetResult();
        }

        public SampleNightLightStore Store { get; }
        public NightLightViewModel Model { get; }
        public List<Action> Pending { get; } = [];

        /// <summary>Let the save timer fire.</summary>
        public void Elapse()
        {
            var due = Pending.ToList();
            Pending.Clear();
            foreach (var action in due)
                action();
        }
    }

    [Fact]
    public void ItOpensShowingWhatIsStored()
    {
        var page = new Page(new SampleNightLightStore(new(NightLightMode.Auto, 3400, false, 35.5, 139.75)));
        var model = page.Model;

        Assert.True(model.IsReady);
        Assert.True(model.IsAuto);
        Assert.False(model.IsOff);
        Assert.Equal(3400, model.Temperature);
        Assert.False(model.UseTimeZone);
        Assert.Equal(35.5m, model.Latitude);
        Assert.Equal(139.75m, model.Longitude);
        Assert.False(model.CanReset);
        Assert.Empty(page.Store.Saved);
    }

    [Fact]
    public void EachRadioButtonWritesItsMode()
    {
        var page = new Page();
        Assert.True(page.Model.IsOff);

        page.Model.IsOn = true;
        page.Elapse();
        page.Model.IsAuto = true;
        page.Elapse();
        page.Model.IsOff = true;
        page.Elapse();

        Assert.Equal(
            [NightLightMode.On, NightLightMode.Auto, NightLightMode.Off],
            page.Store.Saved.Select(saved => saved.Mode));
    }

    [Fact]
    public void AButtonBeingUncheckedChangesNothing()
    {
        // A radio group unchecks the old button as it checks the new one, in either order.
        var page = new Page();
        page.Model.IsOff = false;
        Assert.True(page.Model.IsOff);
        Assert.Empty(page.Pending);
    }

    [Fact]
    public void TheModeNamesAreTheOnesTheCompositorReads()
    {
        Assert.Equal("off", SettingsNightLightStore.ModeName(NightLightMode.Off));
        Assert.Equal("on", SettingsNightLightStore.ModeName(NightLightMode.On));
        Assert.Equal("auto", SettingsNightLightStore.ModeName(NightLightMode.Auto));
    }

    [Theory]
    [InlineData(200, 1000)]
    [InlineData(4449.6, 4450)]
    [InlineData(9000, 6500)]
    public void TheTemperatureIsHeldToTheRange(double asked, int kept)
    {
        var page = new Page();
        page.Model.Temperature = asked;
        Assert.Equal(kept, page.Model.Temperature);
        Assert.Equal(kept, page.Model.TemperatureValue);
    }

    [Fact]
    public void ADraggedSliderIsOneWritePerTickOfTheTimer()
    {
        var page = new Page(new SampleNightLightStore(NightLightSettings.Default with { Mode = NightLightMode.On }));
        for (var kelvin = 4400; kelvin >= 3000; kelvin -= 100)
            page.Model.Temperature = kelvin;

        Assert.Single(page.Pending);
        page.Elapse();
        Assert.Equal(3000, Assert.Single(page.Store.Saved).Temperature);

        // And nothing more until something changes again.
        page.Elapse();
        Assert.Single(page.Store.Saved);
    }

    [Fact]
    public void TheControlsFollowTheMode()
    {
        var page = new Page();
        var model = page.Model;
        Assert.False(model.CanSetTemperature);
        Assert.False(model.CanSetLocation);

        model.IsOn = true;
        Assert.True(model.CanSetTemperature);
        Assert.False(model.CanSetLocation);

        model.IsAuto = true;
        Assert.True(model.CanSetLocation);
        Assert.False(model.CanSetCoordinates);

        model.UseTimeZone = false;
        Assert.True(model.CanSetCoordinates);
        page.Elapse();
        Assert.False(page.Store.Saved[^1].UseTimeZone);
    }

    [Fact]
    public void CoordinatesAreHeldToTheGlobeAndRounded()
    {
        var page = new Page(new SampleNightLightStore(new(NightLightMode.Auto, 4500, false, 0, 0)));
        page.Model.Latitude = 123m;
        page.Model.Longitude = -74.006389m;
        page.Elapse();

        var saved = Assert.Single(page.Store.Saved);
        Assert.Equal(90, saved.Latitude);
        Assert.Equal(-74.0064, saved.Longitude);

        // A cleared box keeps what was there.
        page.Model.Latitude = null;
        Assert.Equal(90m, page.Model.Latitude);
        Assert.Empty(page.Pending);
    }

    [Fact]
    public void ResetPutsBackWhatTheWindowOpenedWith()
    {
        var opened = new NightLightSettings(NightLightMode.Auto, 4000, true, 0, 0);
        var page = new Page(new SampleNightLightStore(opened));
        page.Model.IsOn = true;
        page.Model.Temperature = 2500;
        page.Elapse();
        Assert.True(page.Model.CanReset);

        page.Model.Reset();
        page.Elapse();

        Assert.Equal(opened, page.Store.Saved[^1]);
        Assert.True(page.Model.IsAuto);
        Assert.Equal(4000, page.Model.Temperature);
        Assert.False(page.Model.CanReset);
    }

    [Fact]
    public void AChangeFromElsewhereIsShownAndNotWrittenBack()
    {
        var page = new Page();
        page.Store.ChangeElsewhere(new(NightLightMode.On, 2800, true, 0, 0));

        Assert.True(page.Model.IsOn);
        Assert.Equal(2800, page.Model.Temperature);
        Assert.Empty(page.Pending);
        Assert.Empty(page.Store.Saved);
    }

    [Fact]
    public void WithNoSettingsServiceThePageSaysSoAndStaysShut()
    {
        var page = new Page(new SampleNightLightStore { LoadFailure = new InvalidOperationException("no such name\nmore") });
        Assert.False(page.Model.IsReady);
        Assert.Contains("no such name", page.Model.Status);
        Assert.DoesNotContain("more", page.Model.Status);

        // Nothing is written from a page that never loaded.
        page.Model.IsOn = true;
        Assert.Empty(page.Pending);
    }

    [Fact]
    public void AnOldSettingsServiceGetsItsOwnMessage()
    {
        var page = new Page(new SampleNightLightStore { LoadFailure = new NightLightUnsupportedException() });
        Assert.False(page.Model.IsReady);
        Assert.Contains("wlrix-settings-daemon", page.Model.Status);
    }

    [Fact]
    public void ARefusedWriteIsReported()
    {
        var page = new Page();
        page.Store.NextSaveFailure = new InvalidOperationException("compositor.night_light.mode: refused");
        page.Model.IsOn = true;
        page.Elapse();
        Assert.Contains("refused", page.Model.Status);

        // The next one goes through and clears it.
        page.Model.IsAuto = true;
        page.Elapse();
        Assert.Equal(string.Empty, page.Model.Status);
    }

    [Fact]
    public void ValuesFromTheDaemonAreLaidOverWhatIsKnown()
    {
        var known = new NightLightSettings(NightLightMode.On, 3000, false, 10, 20);
        var merged = SettingsNightLightStore.Merge(known, new Dictionary<string, object?>
        {
            ["compositor.night_light.mode"] = "auto",
            // A whole number somebody typed into the file arrives as an integer.
            ["compositor.night_light.latitude"] = 45L,
            // Removed from the file: back to the default.
            ["compositor.night_light.temperature"] = null,
            ["compositor.mouse.left_handed"] = true,
        });

        Assert.Equal(new NightLightSettings(NightLightMode.Auto, 4500, false, 45, 20), merged);
        Assert.Equal(known, SettingsNightLightStore.Merge(known, new Dictionary<string, object?>()));
    }

    // ── The notebook around it ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplyAndResetAreAboutThePageThatIsShowing()
    {
        var feed = new SampleOutputFeed(SampleOutputFeed.Default());
        var page = new Page();
        var window = new MainWindowViewModel(feed, action => action(), page.Model);
        window.Start();

        // A staged display change: Apply and Reset both have something to do.
        window.Displays[0].ScalePercent = 150;
        Assert.True(window.CanApplyPage);
        Assert.True(window.CanResetPage);

        // With Night Light in front there is nothing to apply, and nothing changed there yet.
        window.SelectedTab = MainWindowViewModel.NightLightTab;
        Assert.False(window.CanApplyPage);
        Assert.False(window.CanResetPage);

        page.Model.IsOn = true;
        page.Elapse();
        Assert.True(window.CanResetPage);

        // Reset here puts the night light back and leaves the staged display change alone.
        window.ResetPage();
        page.Elapse();
        Assert.True(page.Model.IsOff);
        Assert.True(window.IsDirty);

        window.SelectedTab = MainWindowViewModel.DisplaysTab;
        Assert.True(window.CanApplyPage);
        window.ResetPage();
        Assert.False(window.IsDirty);
    }

    [Fact]
    public void HelpIsAboutThePageThatIsShowing()
    {
        var window = new MainWindowViewModel(new SampleOutputFeed(), action => action());
        var displays = window.HelpText;
        window.SelectedTab = MainWindowViewModel.NightLightTab;
        Assert.NotEqual(displays, window.HelpText);
        Assert.Contains("Sunset", window.HelpText);
    }
}
