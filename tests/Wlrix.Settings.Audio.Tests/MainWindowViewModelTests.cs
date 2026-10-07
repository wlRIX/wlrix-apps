// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Audio;
using Wlrix.Settings.Audio.Localization;
using Wlrix.Settings.Audio.Models;
using Wlrix.Settings.Audio.Services;
using Wlrix.Settings.Audio.ViewModels;
using Xunit;

namespace Wlrix.Settings.Audio.Tests;

public class MainWindowViewModelTests
{
    private long _now = 1_000_000;

    private (MainWindowViewModel Model, SampleAudioFeed Feed) Start(bool isPipeWire = true,
        PanelState? state = null)
    {
        var feed = new SampleAudioFeed(isPipeWire, animate: false);
        // Feed events run in place rather than through the dispatcher, and time only moves
        // when a test moves it.
        var model = new MainWindowViewModel(feed, state ?? new PanelState(), action => action(), () => _now);
        model.Start();
        return (model, feed);
    }

    private static DeviceColumnViewModel Column(MainWindowViewModel model, string title) =>
        model.Columns.Single(column => column.Title == title);

    [Fact]
    public void InputsComeBeforeOutputs()
    {
        var (model, _) = Start();
        Assert.Equal(["Analog In", "Analog Out", "Digital Out"], model.Columns.Select(c => c.Title));
        Assert.True(model.Columns[0].IsInput);
        Assert.True(model.Columns[1].IsOutput);
    }

    [Fact]
    public void TheDefaultOutputStartsSelected()
    {
        var (model, _) = Start();
        Assert.Same(Column(model, "Analog Out"), model.SelectedColumn);
        Assert.True(model.SelectedColumn!.IsSelected);
        Assert.Single(model.Columns, column => column.IsSelected);
    }

    [Fact]
    public void SelectingMovesTheOutline()
    {
        var (model, _) = Start();
        model.SelectedColumn = model.Columns[0];
        Assert.True(model.Columns[0].IsSelected);
        Assert.False(Column(model, "Analog Out").IsSelected);

        // There is always a selection while there are devices.
        model.SelectedColumn = null;
        Assert.Same(model.Columns[0], model.SelectedColumn);
    }

    [Fact]
    public void AGroupedSliderMovesBothChannels()
    {
        var (model, feed) = Start();
        var output = Column(model, "Analog Out");
        Assert.True(output.Grouped);

        output.Right = 8;
        Assert.Equal(8, output.Left);
        var device = feed.Devices.Single(d => d.Key == output.Key);
        Assert.Equal([VolumeScale.FromSlider(8), VolumeScale.FromSlider(8)], device.Volumes);
    }

    [Fact]
    public void AnUngroupedSliderMovesOneChannel()
    {
        var (model, feed) = Start();
        var output = Column(model, "Analog Out");
        output.Grouped = false;

        output.Right = 8;
        Assert.Equal(5, output.Left, 3);
        var device = feed.Devices.Single(d => d.Key == output.Key);
        Assert.Equal([VolumeScale.FromSlider(5), VolumeScale.FromSlider(8)], device.Volumes);
    }

    [Fact]
    public void ServerChangesMoveTheSlidersOnceTheyHaveSettled()
    {
        var (model, feed) = Start();
        var output = Column(model, "Analog Out");
        output.Left = 6;

        // Straight after a move, an older value from the server does not drag it back.
        feed.Change(output.Key, d => d with { Volumes = [VolumeScale.FromSlider(2), VolumeScale.FromSlider(2)] });
        Assert.Equal(6, output.Left, 3);

        _now += 1000;
        feed.Change(output.Key, d => d with { Volumes = [VolumeScale.FromSlider(3), VolumeScale.FromSlider(3)] });
        Assert.Equal(3, output.Left, 3);
        Assert.Equal(3, output.Right, 3);
    }

    [Fact]
    public void ASwitchedOffDeviceIsGreyedAndCanBeEnabled()
    {
        var (model, _) = Start();
        var digital = Column(model, "Digital Out");
        Assert.False(digital.IsAvailable);
        Assert.True(digital.CanEnable);
        Assert.False(digital.CanMakeDefault);
        Assert.False(digital.CanChangeRate);

        digital.Enable();
        Assert.True(digital.IsAvailable);
        Assert.False(digital.CanEnable);
        Assert.True(digital.CanMakeDefault);
    }

    [Fact]
    public void MakeDefaultIsOffForTheDefault()
    {
        var (model, feed) = Start();
        var output = Column(model, "Analog Out");
        var digital = Column(model, "Digital Out");
        Assert.False(output.CanMakeDefault);

        digital.Enable();
        digital.MakeDefault();
        Assert.True(output.CanMakeDefault);
        Assert.False(digital.CanMakeDefault);
        Assert.Single(feed.Devices, d => d is { Kind: DeviceKind.Output, IsDefault: true });
    }

    [Fact]
    public void SampleRateNeedsPipeWire()
    {
        var (model, _) = Start(isPipeWire: false);
        var output = Column(model, "Analog Out");
        Assert.False(output.CanChangeRate);
        Assert.All(output.RateChoices, choice => Assert.False(choice.IsEnabled));
    }

    [Fact]
    public void OnlyAllowedRatesCanBeChosen()
    {
        var (model, _) = Start();
        var output = Column(model, "Analog Out");
        Assert.True(output.CanChangeRate);

        var enabled = output.RateChoices.Where(c => c.IsEnabled).Select(c => c.Value);
        // The sample feed allows 44.1 and 48 kHz, besides Automatic.
        Assert.Equal(["0", "44100", "48000"], enabled);
        Assert.True(output.RateChoices[0].IsChecked);

        output.ChooseRate("44100");
        Assert.True(output.RateChoices.Single(c => c.Value == "44100").IsChecked);
        Assert.Equal(Strings.Rate(44100), output.RateText);
    }

    [Fact]
    public void ThePortMenuFollowsTheActivePort()
    {
        var (model, _) = Start();
        var output = Column(model, "Analog Out");
        Assert.True(output.CanChangePort);
        Assert.Equal("Headphone/Speakers", output.Subtitle);

        output.ChoosePort("analog-output-lineout");
        Assert.Equal("Line Out", output.Subtitle);
        Assert.True(output.PortChoices.Single(c => c.Label == "Line Out").IsChecked);
    }

    [Fact]
    public void AnUnpluggedPortCannotBeChosen()
    {
        var (model, _) = Start();
        var input = Column(model, "Analog In");
        Assert.False(input.PortChoices.Single(c => c.Label == "Line In").IsEnabled);
    }

    [Fact]
    public void AnUpdateKeepsTheSelection()
    {
        var (model, feed) = Start();
        var input = model.Columns[0];
        model.SelectedColumn = input;

        feed.Change(input.Key, d => d with { State = DeviceState.Running });
        Assert.Same(input, model.Columns[0]);
        Assert.Same(input, model.SelectedColumn);
        Assert.True(input.IsActive);
    }

    [Fact]
    public void PeaksReachOnlyAMeteredInput()
    {
        var (model, feed) = Start();
        var input = model.Columns[0];
        feed.RaisePeaks(input.Key, 0.5f, 0.25f);
        Assert.Equal(0, input.LeftPeak);

        input.Metering = true;
        Assert.Contains(input.Key, feed.Metered);
        feed.RaisePeaks(input.Key, 0.5f, 0.25f);
        Assert.Equal(0.5f, input.LeftPeak);
        Assert.Equal(0.25f, input.RightPeak);

        input.Metering = false;
        Assert.DoesNotContain(input.Key, feed.Metered);
        Assert.Equal(0, input.LeftPeak);
    }

    [Fact]
    public void MeteringAndGroupingAreRemembered()
    {
        var state = new PanelState();
        var (first, firstFeed) = Start(state: state);
        first.Columns[0].Metering = true;
        first.Columns[0].Grouped = false;
        first.Dispose();

        var (second, feed) = Start(state: state);
        Assert.True(second.Columns[0].Metering);
        Assert.False(second.Columns[0].Grouped);
        Assert.Contains(second.Columns[0].Key, feed.Metered);
        Assert.NotSame(firstFeed, feed);
    }

    [Fact]
    public void MonitorFollowsTheServer()
    {
        var (model, feed) = Start();
        var input = model.Columns[0];
        input.Monitoring = true;
        Assert.True(input.Monitoring);
        Assert.True(feed.Devices[0].IsMonitored);
    }

    [Fact]
    public void TheStatusLineShowsAMissingServerFirst()
    {
        var (model, feed) = Start();
        model.Hint = "a hint";
        Assert.Equal("a hint", model.StatusText);

        feed.RaiseAvailability("no server");
        Assert.Equal("no server", model.StatusText);

        feed.RaiseAvailability(null);
        Assert.Equal("a hint", model.StatusText);
    }

    [Fact]
    public void GainHintsNameThePortAndChannel()
    {
        var (model, _) = Start();
        var output = Column(model, "Analog Out");
        Assert.Equal(Strings.GainHint(DeviceKind.Output, "Headphone/Speakers", ChannelPosition.Right),
            output.RightHint);
        Assert.Contains("Headphone/Speakers", output.RightHint);
    }
}
