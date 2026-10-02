// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Xunit;

namespace Wlrix.Clock.Tests;

public class ClockHandsTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(3, 0, 0, 90, 0, 0)]
    [InlineData(15, 0, 0, 90, 0, 0)]
    [InlineData(12, 30, 0, 15, 180, 0)]
    [InlineData(17, 24, 45, 162.375, 148.5, 270)]
    public void HandsPointWhereAClockWould(int h, int m, int s, double hour, double minute, double second)
    {
        var hands = ClockHands.At(new TimeSpan(h, m, s));
        Assert.Equal(hour, hands.Hour, 6);
        Assert.Equal(minute, hands.Minute, 6);
        Assert.Equal(second, hands.Second, 6);
    }

    [Fact]
    public void TheSecondHandStepsRatherThanSweeps()
    {
        // Half a second in is still the same second: the hand only moves on the redraw.
        var hands = ClockHands.At(new TimeSpan(0, 0, 0, 10, 500));
        Assert.Equal(60, hands.Second, 6);
    }
}

public class ClockTextTests
{
    [Theory]
    [InlineData(-4, 0, "GMT-4")]
    [InlineData(9, 0, "GMT+9")]
    [InlineData(0, 0, "GMT+0")]
    [InlineData(5, 30, "GMT+5:30")]
    [InlineData(-3, -30, "GMT-3:30")]
    [InlineData(5, 45, "GMT+5:45")]
    public void TheZoneIsAnOffsetFromGmt(int hours, int minutes, string expected) =>
        Assert.Equal(expected, ClockText.Zone(new TimeSpan(hours, minutes, 0)));

    [Theory]
    [InlineData("ja-JP", "2026年9月30日水曜日\nGMT-4: 17:24:45")]
    [InlineData("en-US", "Wednesday, September 30, 2026\nGMT-4: 17:24:45")]
    public void TheDateIsTheCulturesLongDate(string culture, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 30, 17, 24, 45, TimeSpan.FromHours(-4));
        Assert.Equal(expected, ClockText.Format(now, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void TheTimeIsTwentyFourHourWhateverTheCulture()
    {
        // en-US would write 3:04:05 AM; the time line does not follow the culture.
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.EndsWith("\nGMT+0: 03:04:05", ClockText.Format(now, CultureInfo.GetCultureInfo("en-US")));
    }
}
