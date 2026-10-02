// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Clock;

/// <summary>
/// Where the three hands point at a given time, in degrees clockwise from twelve.
/// </summary>
/// <remarks>
/// The second hand steps rather than sweeps, as IRIX's did: it is redrawn once a second, and a
/// hand that moved between redraws would only ever be seen jumping anyway. The minute and hour
/// hands do creep with the seconds and minutes below them, so the hour hand is two thirds of the
/// way to five at 4:40 rather than sitting on the four until the hour turns.
/// </remarks>
internal readonly record struct ClockHands(double Hour, double Minute, double Second)
{
    public static ClockHands At(TimeSpan timeOfDay)
    {
        var seconds = timeOfDay.Seconds;
        var minutes = timeOfDay.Minutes + seconds / 60.0;
        var hours = timeOfDay.Hours % 12 + minutes / 60.0;
        return new ClockHands(hours * 30.0, minutes * 6.0, seconds * 6.0);
    }
}
