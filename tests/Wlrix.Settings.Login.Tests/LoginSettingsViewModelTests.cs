using Wlrix.Settings.Login.Core;
using Wlrix.Settings.Login.Services;
using Wlrix.Settings.Login.ViewModels;
using Wlrix.Settings.Outputs;
using Xunit;

namespace Wlrix.Settings.Login.Tests;

/// <summary>The form, and the way it combines the file with the displays plugged in now.</summary>
public class LoginSettingsViewModelTests
{
    private static OutputHead Head(string name, bool enabled = true, int x = 0, double scale = 1.0,
        OutputTransform transform = OutputTransform.Normal)
    {
        OutputMode[] modes = [new(2560, 1440, 143_973, true), new(2560, 1440, 59_951, false), new(1920, 1080, 60_000, false)];
        return new OutputHead(name, "Make", "Model", null, $"{name} monitor", 600, 340, modes,
            enabled ? modes[0] : null, enabled, x, 0, transform, scale, false, false, false, false, 203);
    }

    [Fact]
    public void WhatIsLoadedIsWhatIsWritten()
    {
        var config = new GreeterConfig
        {
            Palette = "gotham",
            Layout = "jp",
            Model = "jp106",
            Variant = ",intl",
            Options = "grp:alt_shift_toggle",
            RepeatDelay = 600,
            RepeatRate = 25,
            Outputs = [new GreeterOutput("DP-1", null, new GreeterMode(2560, 1440, 144), 0, 0, 1.5, "90")],
        };
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(config);
        Assert.Equal(config, model.ToConfig());
    }

    [Fact]
    public void ALayoutTheCatalogDoesNotKnowIsKept()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(new GreeterConfig { Layout = "jp,us" });
        Assert.Equal("jp,us", model.ToConfig().Layout);
    }

    [Fact]
    public void RepeatOffIsARateOfZero()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(new GreeterConfig { RepeatRate = 0 });
        Assert.False(model.RepeatEnabled);
        Assert.Equal(0, model.ToConfig().RepeatRate);
    }

    [Fact]
    public void ConnectedDisplaysJoinTheFileByName()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(new GreeterConfig { Outputs = [new GreeterOutput("DP-1", Scale: 2), new GreeterOutput("DP-9")] });
        model.Merge(new OutputSnapshot([Head("DP-1"), Head("HDMI-A-1")]));

        Assert.Equal(["DP-1", "DP-9", "HDMI-A-1"], model.Outputs.Select(row => row.Name));
        Assert.True(model.Outputs[0].IsSet);
        Assert.True(model.Outputs[0].IsConnected);
        Assert.True(model.Outputs[1].IsDisconnected);
        Assert.False(model.Outputs[2].IsSet);

        // An unset display is not written; the compositor places it itself.
        Assert.Equal(["DP-1", "DP-9"], model.ToConfig().Outputs.Select(output => output.Name));
    }

    [Fact]
    public void AModeTheMonitorDoesNotListStaysChoosable()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(new GreeterConfig { Outputs = [new GreeterOutput("DP-1", Mode: new GreeterMode(1280, 1024, 75))] });
        model.Merge(new OutputSnapshot([Head("DP-1")]));
        Assert.Equal(new GreeterMode(1280, 1024, 75), model.ToConfig().Outputs.Single().Mode);
    }

    [Fact]
    public void RefreshRatesRoundingToTheSameHertzAreOneChoice()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Merge(new OutputSnapshot([Head("DP-1")]));
        // Automatic, 2560x1440@144, 2560x1440@60, 1920x1080@60.
        Assert.Equal(4, model.Outputs.Single().Modes.Count);
    }

    [Fact]
    public void AutomaticLayoutSetsNothingAndForgetsUnpluggedDisplays()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        model.Load(new GreeterConfig { Outputs = [new GreeterOutput("DP-1"), new GreeterOutput("DP-9")] });
        model.Merge(new OutputSnapshot([Head("DP-1")]));
        model.AutomaticDisplays();

        Assert.Equal(["DP-1"], model.Outputs.Select(row => row.Name));
        Assert.Empty(model.ToConfig().Outputs);
    }

    [Fact]
    public void ADisplayIsCopiedAsItIsNow()
    {
        var output = OutputMapping.FromHead(Head("DP-2", x: 2560, scale: 1.25, transform: OutputTransform.Flipped270));
        Assert.Equal(new GreeterOutput("DP-2", true, new GreeterMode(2560, 1440, 144), 2560, 0, 1.25, "flipped-270"), output);
    }

    [Fact]
    public void ASwitchedOffDisplayIsCopiedAsOff()
    {
        using var model = new LoginSettingsViewModel(new SampleOutputFeed());
        var head = Head("HDMI-A-1", enabled: false);
        model.Merge(new OutputSnapshot([head]));
        model.Load(new GreeterConfig { Outputs = [OutputMapping.FromHead(head)] });
        Assert.Equal(false, model.ToConfig().Outputs.Single().Enabled);
    }
}
