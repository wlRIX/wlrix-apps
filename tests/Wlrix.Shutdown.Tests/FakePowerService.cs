using Wlrix.Shutdown.Services;

namespace Wlrix.Shutdown.Tests;

/// <summary>
/// An <see cref="IPowerService"/> that records what it was asked to do instead of doing it.
///
/// The point of the whole design is that the view model reaches logind through one narrow
/// interface, so a test can watch it decide without a system bus anywhere near it -- and
/// without the machine going down when a test passes.
/// </summary>
internal sealed class FakePowerService(PowerCapabilities capabilities, string? failure = null) : IPowerService
{
    /// <summary>Everything available, which is what most of these tests want.</summary>
    public static PowerCapabilities All => new(CanPowerOff: true, CanRestart: true, CanFirmwareSetup: true);

    /// <summary>Whether <see cref="PowerOffAsync"/> was called.</summary>
    public bool PoweredOff { get; private set; }

    /// <summary>The <c>toFirmwareSetup</c> of the <see cref="RestartAsync"/> call, if there was one.</summary>
    public bool? RestartedToFirmwareSetup { get; private set; }

    public Task<PowerCapabilities> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(capabilities);

    public Task<string?> PowerOffAsync()
    {
        PoweredOff = true;
        return Task.FromResult(failure);
    }

    public Task<string?> RestartAsync(bool toFirmwareSetup)
    {
        RestartedToFirmwareSetup = toFirmwareSetup;
        return Task.FromResult(failure);
    }
}
