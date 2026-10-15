// SPDX-License-Identifier: GPL-3.0-or-later
namespace Wlrix.Settings.Mouse.Services;

/// <summary>What the Test button decided a click was.</summary>
internal enum ClickKind
{
    Single,
    Double,
}

/// <summary>
/// The Test button's judgment: was that a double click, at the speed currently set?
///
/// Timed here rather than taken from the toolkit's own double-click detection, because the point
/// of the button is to try the *panel's* setting -- including one the slider has just moved to
/// and nothing else has read yet. A first click cannot be called single until the double-click
/// time has passed without a second, so it is held: <see cref="Press"/> answers a double at once,
/// and <see cref="Expire"/>, called when that time is up, answers the single.
/// </summary>
internal sealed class ClickTest
{
    private TimeSpan? _first;

    /// <summary>Whether a first click is waiting to find out what it was.</summary>
    public bool Pending => _first is not null;

    /// <summary>
    /// A click at <paramref name="now"/>. <see cref="ClickKind.Double"/> if it came within
    /// <paramref name="window"/> of a waiting first click; otherwise null, and it is now the
    /// first click of whatever comes next.
    /// </summary>
    public ClickKind? Press(TimeSpan now, TimeSpan window)
    {
        if (_first is { } first && now - first <= window)
        {
            _first = null;
            return ClickKind.Double;
        }
        _first = now;
        return null;
    }

    /// <summary>
    /// The double-click time ran out. <see cref="ClickKind.Single"/> if a click was waiting,
    /// null if a second click had already settled it.
    /// </summary>
    public ClickKind? Expire()
    {
        if (_first is null)
            return null;
        _first = null;
        return ClickKind.Single;
    }
}
