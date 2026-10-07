// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Settings.Audio.Audio;
using Wlrix.Settings.Audio.Models;
using Xunit;

namespace Wlrix.Settings.Audio.Tests;

public class VolumeScaleTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(2.5)]
    [InlineData(5.0)]
    [InlineData(7.3)]
    [InlineData(10.0)]
    public void SliderPositionsSurviveTheRoundTrip(double position) =>
        Assert.Equal(position, VolumeScale.ToSlider(VolumeScale.FromSlider(position)), 3);

    [Fact]
    public void TenIsUnityGain()
    {
        Assert.Equal(AudioDevice.VolumeNorm, VolumeScale.FromSlider(10));
        Assert.Equal(0u, VolumeScale.FromSlider(0));
        Assert.Equal(AudioDevice.VolumeNorm / 2, VolumeScale.FromSlider(5));
    }

    [Fact]
    public void AnAmplifiedVolumeSitsAtTheTop() =>
        Assert.Equal(10, VolumeScale.ToSlider(AudioDevice.VolumeNorm * 3 / 2));

    [Fact]
    public void EachMarkIsWhereTheScaleDrawsIt()
    {
        // From -2 dB down: 0 dB is full scale exactly, which is a clip (below).
        for (var i = 1; i < VolumeScale.MeterMarks.Count; i++)
        {
            var peak = VolumeScale.DbToPeak(VolumeScale.MeterMarks[i]);
            // -60 dB is the bottom of the scale, and reads as nothing.
            var expected = i == VolumeScale.MeterMarks.Count - 1 ? 0 : VolumeScale.MarkFraction(i);
            Assert.Equal(expected, VolumeScale.MeterFraction(peak), 3);
        }
    }

    [Fact]
    public void FullScaleClipsAndFillsTheMeter()
    {
        Assert.True(VolumeScale.IsClipping(1f));
        Assert.True(VolumeScale.IsClipping(1.2f));
        Assert.False(VolumeScale.IsClipping(0.999f));
        Assert.Equal(1, VolumeScale.MeterFraction(1f));
        // Just under full scale stops at the 0 dB mark, below the clip row.
        Assert.Equal(VolumeScale.MarkFraction(0), VolumeScale.MeterFraction(0.99999f), 3);
    }

    [Fact]
    public void SilenceIsEmpty()
    {
        Assert.Equal(0, VolumeScale.MeterFraction(0));
        Assert.Equal(0, VolumeScale.MeterFraction(VolumeScale.DbToPeak(-90)));
    }

    [Fact]
    public void TheMeterRisesWithTheLevel()
    {
        var last = -1.0;
        for (var db = -60.0; db <= 0; db += 0.5)
        {
            var fraction = VolumeScale.MeterFraction(VolumeScale.DbToPeak(db));
            Assert.True(fraction >= last, $"{db} dB fell to {fraction}");
            last = fraction;
        }
    }

    [Fact]
    public void GroupedMovesEveryChannel() =>
        Assert.Equal([VolumeScale.FromSlider(4), VolumeScale.FromSlider(4)],
            VolumeScale.Move([100, 200], 1, 4, grouped: true));

    [Fact]
    public void UngroupedMovesOneChannel() =>
        Assert.Equal([100u, VolumeScale.FromSlider(4)],
            VolumeScale.Move([100, 200], 1, 4, grouped: false));
}
