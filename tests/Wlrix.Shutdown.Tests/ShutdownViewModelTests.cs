using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Shutdown.Services;
using Wlrix.Shutdown.ViewModels;
using Xunit;

namespace Wlrix.Shutdown.Tests;

/// <summary>
/// What the window offers, and what OK does about it.
///
/// Every one of these runs against a <see cref="FakePowerService"/>: the view model is a plain
/// object, and nothing here touches the dispatcher, a display or the system bus.
/// </summary>
public class ShutdownViewModelTests
{
    /// <summary>A model over <paramref name="power"/>, already told what the machine can do.</summary>
    private static async Task<ShutdownViewModel> ModelAsync(IPowerService power, bool restart = false)
    {
        var model = new ShutdownViewModel(power, NullLogger<ShutdownViewModel>.Instance, restart);
        await model.InitializeAsync();
        return model;
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--restart" }, true)]
    [InlineData(new[] { "--verbose", "--restart" }, true)]
    [InlineData(new[] { "--restart-later" }, false)]
    [InlineData(new[] { "restart" }, false)]
    public void OnlyTheRestartFlagOpensTheRestartForm(string[] args, bool expected) =>
        Assert.Equal(expected, Program.WantsRestart(args));

    [Fact]
    public async Task TheRestartFlagArrivesAsACheckedBox()
    {
        Assert.True((await ModelAsync(new FakePowerService(FakePowerService.All), restart: true)).Restart);
        Assert.False((await ModelAsync(new FakePowerService(FakePowerService.All))).Restart);
    }

    [Fact]
    public async Task TheFirmwareRowNeedsBothARestartAndAMachineThatCanDoIt()
    {
        // Nothing to qualify: the machine is being powered off, not restarted.
        var poweringOff = await ModelAsync(new FakePowerService(FakePowerService.All));
        Assert.False(poweringOff.IsFirmwareSetupVisible);

        var restarting = await ModelAsync(new FakePowerService(FakePowerService.All), restart: true);
        Assert.True(restarting.IsFirmwareSetupVisible);
        Assert.Equal(1, restarting.FirmwareSetupOpacity);
        Assert.True(restarting.IsFirmwareSetupEnabled);

        // A machine that did not boot through EFI: logind answers "na", and there is no such
        // option to offer however the first box is set.
        var noFirmware = await ModelAsync(
            new FakePowerService(FakePowerService.All with { CanFirmwareSetup = false }), restart: true);
        Assert.False(noFirmware.IsFirmwareSetupVisible);
    }

    [Fact]
    public void NothingIsOfferedBeforeLogindHasAnswered()
    {
        // Not initialized: no probe, so no capabilities, so no firmware row even with Restart
        // checked. A window that has not heard from the bus should not offer an option it may
        // have to take away.
        var model = new ShutdownViewModel(
            new FakePowerService(FakePowerService.All), NullLogger<ShutdownViewModel>.Instance, restart: true);
        Assert.False(model.IsFirmwareSetupVisible);
    }

    [Fact]
    public async Task ClearingRestartClearsTheFirmwareChoiceWithIt()
    {
        var model = await ModelAsync(new FakePowerService(FakePowerService.All), restart: true);
        model.RebootToFirmwareSetup = true;

        model.Restart = false;

        Assert.False(model.RebootToFirmwareSetup);
        Assert.False(model.IsFirmwareSetupVisible);

        // Faded out and unclickable, but still holding its space: hiding it any other way
        // would shrink the column the two labels share and move the Restart checkbox.
        Assert.Equal(0, model.FirmwareSetupOpacity);
        Assert.False(model.IsFirmwareSetupEnabled);

        // And checking it again does not bring back a choice the user cannot see they made.
        model.Restart = true;
        Assert.False(model.RebootToFirmwareSetup);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OkActsOnTheCheckboxes(bool restart, bool firmware)
    {
        var power = new FakePowerService(FakePowerService.All);
        var model = await ModelAsync(power, restart);
        model.RebootToFirmwareSetup = firmware;

        await model.ConfirmAsync();

        if (restart)
        {
            Assert.False(power.PoweredOff);
            Assert.Equal(firmware, power.RestartedToFirmwareSetup);
        }
        else
        {
            Assert.True(power.PoweredOff);
            Assert.Null(power.RestartedToFirmwareSetup);
        }
    }

    [Fact]
    public async Task OkSaysWhyWhenTheMachineIsStillRunning()
    {
        const string why = "Interactive authentication required.";
        var model = await ModelAsync(new FakePowerService(FakePowerService.All, failure: why));

        string? reported = null;
        model.ShowError += message => reported = message;

        await model.ConfirmAsync();

        Assert.Equal(why, reported);
        // Usable again: the reason has to appear over a window that is still there.
        Assert.False(model.Busy);
    }

    [Fact]
    public async Task AnUnavailableActionIsRefusedBeforeItIsAttempted()
    {
        var power = new FakePowerService(FakePowerService.All with { CanPowerOff = false });
        var model = await ModelAsync(power);

        string? reported = null;
        model.ShowError += message => reported = message;

        await model.ConfirmAsync();

        // A sentence saying which one and why, rather than an OK that quietly does nothing.
        Assert.False(power.PoweredOff);
        Assert.False(string.IsNullOrWhiteSpace(reported));
    }
}
