using Wlrix.Settings.Displays.Models;
using Wlrix.Settings.Displays.Services;
using Wlrix.Settings.Displays.ViewModels;
using Xunit;

namespace Wlrix.Settings.Displays.Tests;

public class MainWindowViewModelTests
{
    private static (MainWindowViewModel Model, SampleOutputFeed Feed) Start(OutputSnapshot? snapshot = null)
    {
        var feed = new SampleOutputFeed(snapshot ?? SampleOutputFeed.Default());
        // Feed events run in place rather than through the dispatcher.
        var model = new MainWindowViewModel(feed, action => action());
        model.Start();
        return (model, feed);
    }

    private static OutputSnapshot OneDisplay() =>
        new([SampleOutputFeed.Default().Heads[2] with { X = 0, Y = 0 }]);

    [Fact]
    public void ASingleDisplayHasNoArrangementAndIsSelected()
    {
        var (model, _) = Start(OneDisplay());
        Assert.False(model.HasMultiple);
        Assert.Same(model.Displays[0], model.SelectedDisplay);

        // And cannot be unselected.
        model.SelectedDisplay = null;
        Assert.Same(model.Displays[0], model.SelectedDisplay);
    }

    [Fact]
    public void SeveralDisplaysHaveAnArrangement()
    {
        var (model, _) = Start();
        Assert.True(model.HasMultiple);
        Assert.Equal(3, model.Displays.Count);
        Assert.False(model.IsDirty);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(115, 115)]
    [InlineData(1000, 300)]
    public void ScaleIsHeldBetweenHalfAndTriple(double asked, double kept)
    {
        var (model, _) = Start();
        var display = model.Displays[0];
        display.ScalePercent = asked;
        Assert.Equal(kept, display.ScalePercent);
        Assert.Equal(kept / 100, display.Scale, 6);
    }

    [Fact]
    public void HdrCannotBeTurnedOnWhereItIsNotSupported()
    {
        var (model, _) = Start();
        var plain = model.Displays.Single(d => d.Name == "DP-2");
        var capable = model.Displays.Single(d => d.Name == "DP-3");

        Assert.False(plain.HdrSupported);
        Assert.NotNull(plain.HdrTip);
        plain.Hdr = true;
        Assert.False(plain.Hdr);

        Assert.True(capable.HdrSupported);
        Assert.Null(capable.HdrTip);
        capable.Hdr = true;
        Assert.True(capable.Hdr);
    }

    [Fact]
    public void TheRefreshRatesFollowTheResolution()
    {
        var (model, _) = Start();
        var display = model.Displays.Single(d => d.Name == "DP-3");
        Assert.Equal(3, display.RefreshRates.Count);

        display.Resolution = display.Resolutions.Single(r => r.Width == 1920);
        Assert.Single(display.RefreshRates);
        Assert.Equal(1920, display.Mode!.Width);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ResolutionsAreLargestFirstAndNamedByAspect()
    {
        var (model, _) = Start();
        var display = model.Displays.Single(d => d.Name == "DP-1");
        Assert.Equal(1920, display.Resolutions[0].Width);
        Assert.Contains("16:10", display.Resolutions[0].Label);
    }

    [Fact]
    public void TheLastEnabledDisplayCannotBeTurnedOff()
    {
        var (model, _) = Start(OneDisplay());
        var display = model.Displays[0];
        Assert.False(display.CanToggleEnabled);
        display.Enabled = false;
        Assert.True(display.Enabled);
    }

    [Fact]
    public void TurningADisplayOffClosesUpTheLayout()
    {
        var (model, _) = Start();
        model.Displays.Single(d => d.Name == "DP-2").Enabled = false;

        var left = model.Displays.Single(d => d.Name == "DP-1").Box;
        var right = model.Displays.Single(d => d.Name == "DP-3").Box;
        Assert.True(left.Touches(right));
    }

    [Fact]
    public void ScalingADisplayKeepsTheLayoutTogether()
    {
        var (model, _) = Start();
        model.Displays.Single(d => d.Name == "DP-2").ScalePercent = 200;

        var boxes = model.Displays.Select(d => d.Box).ToList();
        foreach (var box in boxes)
        {
            Assert.DoesNotContain(boxes.Where(b => b != box), b => b.Overlaps(box));
            Assert.Contains(boxes.Where(b => b != box), b => b.Touches(box));
        }
    }

    [Fact]
    public void DraggingADisplaySnapsIt()
    {
        var (model, _) = Start();
        var dp3 = model.Displays.Single(d => d.Name == "DP-3");
        // Under DP-2, a little off.
        model.MoveDisplay(dp3, 1300, 1500);
        var dp2 = model.Displays.Single(d => d.Name == "DP-2").Box;
        Assert.True(dp3.Box.Touches(dp2));
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task ApplyingSendsTheStagedLayoutAndKeepsIt()
    {
        var (model, feed) = Start();
        model.Displays[1].ScalePercent = 150;
        Assert.True(model.CanApply);

        await model.ApplyAsync();

        Assert.Single(feed.Applied);
        Assert.Equal(1.5, feed.Current.Heads[1].Scale, 6);
        Assert.False(model.IsDirty);
        Assert.Null(model.Status);
    }

    [Fact]
    public async Task NotKeepingPutsTheOldLayoutBack()
    {
        var (model, feed) = Start();
        var original = feed.Current;
        model.ConfirmKeep = () => Task.FromResult(false);
        model.Displays[1].ScalePercent = 150;

        await model.ApplyAsync();

        Assert.Equal(2, feed.Applied.Count);
        Assert.Equal(original.Heads[1].Scale, feed.Current.Heads[1].Scale, 6);
        Assert.Equal(1.0, model.Displays[1].Scale, 6);
        Assert.False(model.IsDirty);
        Assert.NotNull(model.Status);
    }

    [Fact]
    public async Task ARefusedConfigurationKeepsTheEdits()
    {
        var (model, feed) = Start();
        feed.NextResult = ApplyResult.Failed;
        model.Displays[1].ScalePercent = 150;

        await model.ApplyAsync();

        Assert.Empty(feed.Applied);
        Assert.True(model.IsDirty);
        Assert.NotNull(model.Status);
    }

    [Fact]
    public void ResetDropsTheEdits()
    {
        var (model, _) = Start();
        model.Displays[1].ScalePercent = 150;
        model.Reset();
        Assert.False(model.IsDirty);
        Assert.Equal(100, model.Displays[1].ScalePercent);
    }

    [Fact]
    public void AHotplugReloadsAndSaysSo()
    {
        var (model, feed) = Start();
        model.Displays[1].ScalePercent = 150;

        feed.Replace(new OutputSnapshot(feed.Current.Heads.Take(2).ToList()));

        Assert.Equal(2, model.Displays.Count);
        Assert.False(model.IsDirty);
        Assert.NotNull(model.Status);
    }

    [Fact]
    public void ANewSnapshotOfTheSameDisplaysKeepsTheEdits()
    {
        var (model, feed) = Start();
        model.Displays[1].ScalePercent = 150;

        feed.Replace(feed.Current);

        Assert.True(model.IsDirty);
        Assert.Equal(150, model.Displays[1].ScalePercent);
    }

    [Fact]
    public void TheCountdownRunsOut()
    {
        var confirm = new ConfirmViewModel(seconds: 2);
        Assert.False(confirm.Tick());
        Assert.True(confirm.Tick());
        Assert.Equal(0, confirm.SecondsLeft);
    }
}
