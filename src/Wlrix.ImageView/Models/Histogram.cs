// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;
using Wlrix.Common.Imaging;

namespace Wlrix.ImageView.Models;

/// <summary>Which values a histogram shows.</summary>
public enum HistogramChannel
{
    /// <summary>Red, green and blue together.</summary>
    Rgb,
    Red,
    Green,
    Blue,
    Luminance,
}

/// <summary>The lowest, highest, mean and median value of one channel.</summary>
public readonly record struct HistogramStats(int Min, int Max, double Mean, int Median);

/// <summary>How many pixels have each 8-bit value, per channel.</summary>
public sealed class Histogram
{
    public const int Bins = 256;

    private Histogram(int[] red, int[] green, int[] blue, int[] luminance, long pixelCount)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Luminance = luminance;
        PixelCount = pixelCount;
    }

    public int[] Red { get; }
    public int[] Green { get; }
    public int[] Blue { get; }
    public int[] Luminance { get; }

    /// <summary>How many pixels were counted. Fully transparent ones are not.</summary>
    public long PixelCount { get; }

    /// <summary>Counts a bitmap's pixels. It must be BGRA8888 and unpremultiplied.</summary>
    public static Histogram FromBitmap(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType == SKAlphaType.Premul)
            throw new ArgumentException("Expected unpremultiplied BGRA8888 pixels.", nameof(bitmap));
        return FromBgra(bitmap.GetPixelSpan(), bitmap.RowBytes, bitmap.Width, bitmap.Height);
    }

    /// <summary>Counts unpremultiplied BGRA pixels.</summary>
    /// <remarks>
    /// A pixel with no alpha at all is skipped. Its color bytes are whatever the encoder left
    /// there, usually black, and counting them would put a spike at zero for every
    /// transparent margin that nobody can see.
    /// </remarks>
    public static Histogram FromBgra(ReadOnlySpan<byte> pixels, int rowBytes, int width, int height)
    {
        var red = new int[Bins];
        var green = new int[Bins];
        var blue = new int[Bins];
        var luminance = new int[Bins];
        long count = 0;

        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                if (row[x + 3] == 0)
                    continue;

                int b = row[x], g = row[x + 1], r = row[x + 2];
                red[r]++;
                green[g]++;
                blue[b]++;
                luminance[Luma(r, g, b)]++;
                count++;
            }
        }

        return new Histogram(red, green, blue, luminance, count);
    }

    /// <summary>Rec. 601 luma, the weighting IRIX's tools used; the same as an SGI gray file stores.</summary>
    internal static int Luma(int r, int g, int b) => SgiImage.Luma(r, g, b);

    /// <summary>
    /// The counts for <paramref name="channel"/>. For <see cref="HistogramChannel.Rgb"/> that
    /// is the three color channels added together.
    /// </summary>
    public int[] Counts(HistogramChannel channel) => channel switch
    {
        HistogramChannel.Red => Red,
        HistogramChannel.Green => Green,
        HistogramChannel.Blue => Blue,
        HistogramChannel.Luminance => Luminance,
        _ => Enumerable.Range(0, Bins).Select(i => Red[i] + Green[i] + Blue[i]).ToArray(),
    };

    public HistogramStats Stats(HistogramChannel channel)
    {
        var counts = Counts(channel);
        long total = 0;
        double sum = 0;
        int min = -1, max = -1;
        for (var value = 0; value < Bins; value++)
        {
            if (counts[value] == 0)
                continue;
            if (min < 0)
                min = value;
            max = value;
            total += counts[value];
            sum += (double)value * counts[value];
        }

        if (total == 0)
            return new HistogramStats(0, 0, 0, 0);

        // The lower median: the first value at which half the samples have been passed.
        var half = (total + 1) / 2;
        long seen = 0;
        var median = 0;
        for (var value = 0; value < Bins; value++)
        {
            seen += counts[value];
            if (seen >= half)
            {
                median = value;
                break;
            }
        }

        return new HistogramStats(min, max, sum / total, median);
    }
}
