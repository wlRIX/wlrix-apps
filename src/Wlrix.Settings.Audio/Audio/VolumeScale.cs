// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Models;

namespace Wlrix.Settings.Audio.Audio;

/// <summary>
/// The panel's two scales: gain sliders marked 0 to 10, and level meters marked in decibels.
/// </summary>
internal static class VolumeScale
{
    /// <summary>The top of a gain slider: 10 is 100%, unity gain.</summary>
    public const double SliderMax = 10;

    /// <summary>
    /// A slider position for a volume. Linear on the server's volume, as pavucontrol's slider
    /// is: PulseAudio's volume is already a cubic curve, so equal steps sound roughly equally
    /// loud. Anything louder than 100% (another mixer may have amplified it) sits at the top.
    /// </summary>
    public static double ToSlider(uint volume) =>
        Math.Clamp(volume / (double)AudioDevice.VolumeNorm * SliderMax, 0, SliderMax);

    /// <summary>The volume for a slider position.</summary>
    public static uint FromSlider(double position) =>
        (uint)Math.Round(Math.Clamp(position, 0, SliderMax) / SliderMax * AudioDevice.VolumeNorm);

    /// <summary>
    /// The decibel marks down the meters' scale, below the clip row. As on the IRIX panel they
    /// are evenly spaced although the steps are not, so the scale spends its height where a
    /// level is worth reading: four marks in the top 10 dB, five in the 50 below.
    /// </summary>
    public static IReadOnlyList<int> MeterMarks { get; } = [0, -2, -4, -7, -10, -20, -30, -40, -50, -60];

    /// <summary>The share of the meter's height each mark, and the clip row, take.</summary>
    public static double MarkStep => 1.0 / MeterMarks.Count;

    /// <summary>
    /// How far up the meter (0 to 1) a mark sits. The bottom mark is at 0; 0 dB is one step
    /// below the top, and the top step is the clip row.
    /// </summary>
    public static double MarkFraction(int index) => (MeterMarks.Count - 1 - index) * MarkStep;

    /// <summary>Whether a peak is full scale or more, which lights the clip row.</summary>
    public static bool IsClipping(float peak) => peak >= 1f;

    /// <summary>
    /// How far up the meter (0 to 1) a peak reaches, interpolating in decibels between the
    /// marks. A clipped peak fills it.
    /// </summary>
    public static double MeterFraction(float peak)
    {
        if (IsClipping(peak))
            return 1;
        if (peak <= 0)
            return 0;

        var db = 20 * Math.Log10(peak);
        var bottom = MeterMarks[^1];
        if (db <= bottom)
            return 0;

        // Walk down the marks to the pair the level falls between.
        for (var i = 0; i < MeterMarks.Count - 1; i++)
        {
            int upper = MeterMarks[i], lower = MeterMarks[i + 1];
            if (db >= lower)
            {
                var t = (db - lower) / (upper - lower);
                return MarkFraction(i + 1) + t * MarkStep;
            }
        }
        return 0;
    }

    /// <summary>The fraction at which the meter turns from green to yellow (−7 dB).</summary>
    public static double YellowFrom => MeterFraction(DbToPeak(-7));

    /// <summary>The fraction at which the meter turns from yellow to red (−2 dB).</summary>
    public static double RedFrom => MeterFraction(DbToPeak(-2));

    public static float DbToPeak(double db) => (float)Math.Pow(10, db / 20);

    /// <summary>
    /// The volumes to send when one channel's slider moves to <paramref name="position"/>.
    /// Grouped, every channel follows it; otherwise only that channel changes. A mono device
    /// has one slider and one channel, so the two are the same.
    /// </summary>
    public static uint[] Move(IReadOnlyList<uint> current, int channel, double position, bool grouped)
    {
        var volume = FromSlider(position);
        var result = current.ToArray();
        if (grouped)
            Array.Fill(result, volume);
        else if (channel >= 0 && channel < result.Length)
            result[channel] = volume;
        return result;
    }
}
