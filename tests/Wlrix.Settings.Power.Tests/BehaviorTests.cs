// SPDX-License-Identifier: GPL-3.0-or-later
using Wlrix.Settings.Power.Services;
using Wlrix.Settings.Power.ViewModels;
using Xunit;

namespace Wlrix.Settings.Power.Tests;

public class LidChoiceTests
{
    [Fact]
    public void EveryActionWlrixIdleKnowsIsOffered()
    {
        // wlrix-idle's LidAction, as serde spells it; a value missing here could never be chosen,
        // and one wlrix-idle does not know would be refused by --check-config.
        Assert.Equal(
            ["suspend", "hibernate", "lock", "blank", "nothing"],
            LidChoice.All.Select(choice => choice.Value));
    }

    [Fact]
    public void AnUnknownValueShowsTheDefault()
    {
        Assert.Equal("hibernate", LidChoice.Find("hibernate", "suspend").Value);
        Assert.Equal("suspend", LidChoice.Find("shutdown", "suspend").Value);
        Assert.Equal("nothing", LidChoice.Find(null, "nothing").Value);
    }
}

public class LidTests
{
    [Fact]
    public void ALidIsADirectoryUnderAcpi()
    {
        var directory = Directory.CreateTempSubdirectory("wlrix-power-lid-");
        try
        {
            Assert.False(Lid.Present(directory.FullName), "an empty directory is a desktop");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "LID0"));
            Assert.True(Lid.Present(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
        Assert.False(Lid.Present(directory.FullName), "no directory, no lid");
    }
}

public class IdleTests
{
    [Fact]
    public void DurationsReadAsPeopleSayThem()
    {
        // The English catalog, whatever the machine running the tests speaks.
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            string Say(long seconds) => Wlrix.Settings.Power.Localization.Strings.Duration(seconds);
            Assert.Equal("Never", Say(0));
            Assert.Equal("1 minute", Say(60));
            Assert.Equal("5 minutes", Say(300));
            Assert.Equal("1 hour", Say(3600));
            Assert.Equal("2 hours", Say(7200));
            Assert.Equal("90 seconds", Say(90));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }
        Assert.Equal(0L, DurationChoice.Presets[0]);
        Assert.Contains(180L, DurationChoice.Presets);
        Assert.Contains(3600L, DurationChoice.Presets);
    }

    [Fact]
    public void AnOddValueFromAFileIsKeptNotReplaced()
    {
        var durations = new System.Collections.ObjectModel.ObservableCollection<DurationChoice>(
            DurationChoice.Presets.Select(DurationChoice.Of));
        var profile = new IdleProfile("plugged_in", durations, _ => { });
        profile.Show(dim: 90, screenOff: 900, locks: false, sleep: 0);
        Assert.Equal(90L, profile.Dim.Seconds);
        // In order, between one and two minutes.
        Assert.Equal(90L, durations[2].Seconds);
        Assert.Equal(new Dictionary<string, object>
        {
            ["idle.plugged_in.dim_after_secs"] = 90L,
            ["idle.plugged_in.screen_off_after_secs"] = 900L,
            ["idle.plugged_in.lock"] = false,
            ["idle.plugged_in.suspend_after_secs"] = 0L,
        }, profile.Values());
    }

    [Fact]
    public void OnlyTheComputersOwnBatteryCounts()
    {
        var root = Directory.CreateTempSubdirectory("wlrix-power-supply-");
        try
        {
            void Supply(string name, string type, string? scope)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root.FullName, name));
                File.WriteAllText(Path.Combine(dir.FullName, "type"), type + "\n");
                if (scope is not null)
                    File.WriteAllText(Path.Combine(dir.FullName, "scope"), scope + "\n");
            }
            Supply("AC", "Mains", null);
            Supply("hidpp_battery_0", "Battery", "Device");
            Assert.False(Battery.Present(root.FullName), "a mouse's battery does not run the computer");
            Supply("BAT0", "Battery", null);
            Assert.True(Battery.Present(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
