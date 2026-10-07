// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Services;
using Wlrix.Settings.Audio.ViewModels;
using Xunit;

namespace Wlrix.Settings.Audio.Tests;

/// <summary>The View, Options and Default menus, and what of them survives a restart.</summary>
public class ViewMenuTests
{
    private static (MainWindowViewModel Model, SampleAudioFeed Feed) Start(PanelState? state = null)
    {
        var feed = new SampleAudioFeed(animate: false);
        var model = new MainWindowViewModel(feed, state ?? new PanelState(), action => action());
        model.Start();
        return (model, feed);
    }

    private static DeviceColumnViewModel Column(MainWindowViewModel model, string title) =>
        model.Columns.Single(column => column.Title == title);

    private static IEnumerable<string> Shown(MainWindowViewModel model) =>
        model.ShownColumns.Select(column => column.Title);

    [Fact]
    public void EverythingIsShownAtFirst()
    {
        var (model, _) = Start();
        Assert.Equal(["Analog In", "Analog Out", "Digital Out"], Shown(model));
        Assert.True(model.ShowDefaultInput);
        Assert.True(model.ShowDefaultOutput);
        Assert.True(model.ShowQuickHelp);
    }

    [Fact]
    public void ADeviceCanBeHidden()
    {
        var (model, _) = Start();
        model.SetVisible(Column(model, "Digital Out"), false);
        Assert.Equal(["Analog In", "Analog Out"], Shown(model));
        // Still listed, for the View menu to turn it back on.
        Assert.Equal(3, model.Columns.Count);
    }

    [Fact]
    public void TheDefaultsStayShownWhileFollowed()
    {
        var (model, _) = Start();
        foreach (var column in model.Columns.ToList())
            model.SetVisible(column, false);
        Assert.Equal(["Analog In", "Analog Out"], Shown(model));

        model.ShowDefaultInput = false;
        Assert.Equal(["Analog Out"], Shown(model));
        model.ShowDefaultOutput = false;
        Assert.Empty(model.ShownColumns);
    }

    [Fact]
    public void AFollowedDefaultMovesWithTheDefault()
    {
        var (model, _) = Start();
        foreach (var column in model.Columns.ToList())
            model.SetVisible(column, false);
        var digital = Column(model, "Digital Out");
        digital.Enable();
        digital.MakeDefault();
        Assert.Equal(["Analog In", "Digital Out"], Shown(model));
    }

    [Fact]
    public void HidingTheSelectedDeviceSelectsAShownOne()
    {
        var (model, _) = Start();
        model.ShowDefaultOutput = false;
        var output = Column(model, "Analog Out");
        Assert.Same(output, model.SelectedColumn);

        model.SetVisible(output, false);
        Assert.NotSame(output, model.SelectedColumn);
        Assert.False(output.IsSelected);
        Assert.Contains(model.SelectedColumn!, model.ShownColumns);
        Assert.True(model.SelectedColumn!.IsSelected);
    }

    [Fact]
    public void TheDefaultMenuListsRealDevicesOnly()
    {
        var (model, _) = Start();
        Assert.Equal(["Analog In"], model.Inputs.Select(c => c.Title));
        // Digital Out is switched off by its card's profile, so it cannot be the default yet.
        Assert.Equal(["Analog Out"], model.Outputs.Select(c => c.Title));

        Column(model, "Digital Out").Enable();
        Assert.Equal(["Analog Out", "Digital Out"], model.Outputs.Select(c => c.Title));
    }

    [Fact]
    public void TheViewAndOptionsSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wlrix-audio-test-{Guid.NewGuid():N}.json");
        try
        {
            var (first, _) = Start(PanelState.Load(path));
            first.SetVisible(Column(first, "Digital Out"), false);
            first.ShowDefaultInput = false;
            first.ShowQuickHelp = false;
            Column(first, "Analog Out").Grouped = false;
            first.Dispose();

            var (second, _) = Start(PanelState.Load(path));
            Assert.False(Column(second, "Digital Out").Visible);
            Assert.Equal(["Analog In", "Analog Out"], Shown(second));
            Assert.False(second.ShowDefaultInput);
            Assert.True(second.ShowDefaultOutput);
            Assert.False(second.ShowQuickHelp);
            Assert.False(Column(second, "Analog Out").Grouped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADamagedFileStartsFresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wlrix-audio-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var (model, _) = Start(PanelState.Load(path));
            Assert.Equal(3, model.ShownColumns.Count);
            Assert.True(model.ShowQuickHelp);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
