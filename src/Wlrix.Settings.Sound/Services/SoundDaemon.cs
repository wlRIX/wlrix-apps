// SPDX-License-Identifier: GPL-3.0-or-later
using Tmds.DBus.Protocol;
using Wlrix.Settings.Sound.DBus;

namespace Wlrix.Settings.Sound.Services;

/// <summary>An installed sound theme: its directory name, and what it calls itself.</summary>
public sealed record SoundTheme(string Id, string Name);

/// <summary>
/// <c>wlrix-sound</c>'s bus interface: the themes it can find, and a sound played on request.
/// </summary>
/// <remarks>
/// Only what the daemon alone knows. The settings themselves -- which theme, which kinds of
/// sound, which output -- are read and written through the settings daemon like every other
/// panel's, and <c>wlrix-sound</c> follows the file.
/// </remarks>
public sealed class SoundDaemon
{
    private const string BusName = "com.wlrix.Sound";
    private const string ObjectPath = "/com/wlrix/Sound";

    private readonly Sound1 _proxy;

    private SoundDaemon(Sound1 proxy) => _proxy = proxy;

    /// <summary>
    /// The daemon on the session bus. Nothing is asked of it yet: it is bus-activated, so the
    /// first call starts it if the session has not.
    /// </summary>
    public static SoundDaemon Connect()
    {
        var connection = DBusConnection.Session
            ?? throw new InvalidOperationException("no session bus to connect to");
        return new SoundDaemon(new Sound1(connection, BusName, ObjectPath));
    }

    /// <summary>Every installed theme, sorted by display name.</summary>
    public async Task<IReadOnlyList<SoundTheme>> ListThemesAsync()
    {
        var themes = await _proxy.ListThemesAsync().ConfigureAwait(false);
        return [.. themes.Select(theme => new SoundTheme(theme.Item1, theme.Item2))];
    }

    /// <summary>
    /// Plays <paramref name="name"/> from <paramref name="theme"/> (empty for the configured one),
    /// whatever the switches say. What a checkbox turning on, or a new theme, sounds like.
    /// </summary>
    public Task PreviewAsync(string theme, string name) => _proxy.PreviewAsync(theme, name);
}
