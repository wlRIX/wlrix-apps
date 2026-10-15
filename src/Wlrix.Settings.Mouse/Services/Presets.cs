// SPDX-License-Identifier: GPL-3.0-or-later
namespace Wlrix.Settings.Mouse.Services;

/// <summary>
/// What the rows of diamonds stand for.
///
/// IRIX's panel offered a handful of fixed positions and hid the sliders behind "Finer Control";
/// a diamond is just a slider value somebody chose in advance. Picking one sets the slider, and
/// a slider moved off every preset leaves no diamond lit, so the row never claims a setting that
/// is not the one in force.
/// </summary>
internal static class Presets
{
    /// <summary>
    /// libinput pointer speeds, Slow to Fast. The middle one is 0, libinput's own default, so a
    /// panel that has never been touched shows the middle diamond as IRIX's did.
    /// </summary>
    public static IReadOnlyList<double> Acceleration { get; } = [-1.0, -0.5, 0.0, 0.5, 1.0];

    /// <summary>
    /// Double-click times in milliseconds, Slow to Fast. "Slow" is a slow *clicker*, so the
    /// longest gap; the middle is the compositor's default.
    /// </summary>
    public static IReadOnlyList<int> DoubleClick { get; } = [600, 400, 250];

    /// <summary>The preset <paramref name="value"/> is, or -1 when it is between them.</summary>
    public static int IndexOf(IReadOnlyList<double> presets, double value)
    {
        for (var i = 0; i < presets.Count; i++)
        {
            // Close enough for a value that went through TOML and back, not so loose that a
            // slider nudged one step off a preset still lights its diamond.
            if (Math.Abs(presets[i] - value) < 0.005)
                return i;
        }
        return -1;
    }

    /// <inheritdoc cref="IndexOf(IReadOnlyList{double}, double)"/>
    public static int IndexOf(IReadOnlyList<int> presets, int value)
    {
        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i] == value)
                return i;
        }
        return -1;
    }
}
