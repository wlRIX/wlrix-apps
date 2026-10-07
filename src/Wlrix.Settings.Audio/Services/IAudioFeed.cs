// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Models;

namespace Wlrix.Settings.Audio.Services;

/// <summary>
/// Where the panel gets its devices from and sends changes to. The real one speaks libpulse;
/// <see cref="SampleAudioFeed"/> stands in for <c>--demo</c> and the tests.
///
/// The setters are fire-and-forget. Whatever they change comes back as the next
/// <see cref="SnapshotReceived"/>, which is the only place the panel learns the truth: a
/// change refused by the server simply never shows up.
/// </summary>
public interface IAudioFeed : IDisposable
{
    /// <summary>
    /// Raised with every device whenever anything about them changes, and once on connecting.
    /// May be raised on any thread.
    /// </summary>
    event Action<AudioSnapshot>? SnapshotReceived;

    /// <summary>
    /// Raised with a reason when there is no sound server to talk to, and with null when one is
    /// back. May be raised on any thread.
    /// </summary>
    event Action<string?>? AvailabilityChanged;

    /// <summary>
    /// The latest peak of a metered input, per channel from 0 to 1 (1 is full scale, and more
    /// means clipping), about 25 times a second. Raised on any thread.
    /// </summary>
    event Action<string, float, float>? PeaksReceived;

    void Start();

    /// <summary>Set the per-channel volumes, in the order of the device's channels.</summary>
    void SetVolume(string key, IReadOnlyList<uint> volumes);

    void SetMute(string key, bool muted);

    /// <summary>Make the device where new streams go (an output) or come from (an input).</summary>
    void SetDefault(string key);

    void SetPort(string key, string port);

    /// <summary>Switch the card's profile so a placeholder device exists.</summary>
    void EnableDevice(string key);

    void SetCardProfile(uint card, string profile);

    /// <summary>Force PipeWire's graph to one sample rate; 0 lets it choose again.</summary>
    void SetForcedRate(int rate);

    /// <summary>Start or stop the peak stream behind an input's meters.</summary>
    void SetMetering(string key, bool on);

    /// <summary>Play an input through the default output, or stop.</summary>
    void SetMonitor(string key, bool on);
}
