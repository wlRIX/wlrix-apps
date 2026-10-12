// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;
using Wlrix.ImageView.Models;
using Wlrix.ImageView.Services;
using Xunit;

namespace Wlrix.ImageView.Tests;

public class SgiImageTests
{
    /// <summary>A 3×2 test card in top-down BGRA: every pixel different, one half transparent.</summary>
    private static byte[] Card(bool gray = false, bool alpha = false)
    {
        byte[][] colors =
        [
            [10, 20, 30], [40, 50, 60], [70, 80, 90],
            [100, 110, 120], [130, 140, 150], [160, 170, 180],
        ];
        var bgra = new byte[colors.Length * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            bgra[i * 4] = gray ? c[0] : c[2];
            bgra[i * 4 + 1] = c[0];
            bgra[i * 4 + 2] = c[0];
            if (!gray)
            {
                bgra[i * 4 + 1] = c[1];
                bgra[i * 4 + 2] = c[0];
            }
            bgra[i * 4 + 3] = alpha && i % 2 == 0 ? (byte)128 : (byte)255;
        }
        return bgra;
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    public void Round_trips(int channels, bool compress)
    {
        var gray = channels <= 2;
        var alpha = channels is 2 or 4;
        var card = Card(gray, alpha);

        var file = SgiImage.Encode(card, 3, 2, channels, compress, "card");
        var decoded = SgiImage.Decode(file);

        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(compress, decoded.Compressed);
        Assert.Equal(card, decoded.Bgra);
    }

    [Fact]
    public void Chooses_the_fewest_channels()
    {
        Assert.Equal(1, SgiImage.ChooseChannels(Card(gray: true)));
        Assert.Equal(2, SgiImage.ChooseChannels(Card(gray: true, alpha: true)));
        Assert.Equal(3, SgiImage.ChooseChannels(Card()));
        Assert.Equal(4, SgiImage.ChooseChannels(Card(alpha: true)));
    }

    [Fact]
    public void Reads_a_hand_written_rle_file()
    {
        // 4×1 gray, RLE: a run of three 200s, then one literal 7.
        var file = new byte[512 + 8 + 5];
        file[0] = 0x01; file[1] = 0xda;      // magic
        file[2] = 1;                          // RLE
        file[3] = 1;                          // 8 bits
        file[5] = 2;                          // two dimensions
        file[7] = 4;                          // width
        file[9] = 1;                          // height
        file[11] = 1;                         // one channel
        file[19] = 255;                       // pixmax
        file[512 + 2] = 0x02; file[512 + 3] = 0x08; // offset 520
        file[512 + 7] = 5;                    // length
        byte[] row = [0x03, 200, 0x81, 7, 0x00];
        row.CopyTo(file, 520);

        var decoded = SgiImage.Decode(file);

        Assert.Equal(new byte[] { 200, 200, 200, 255, 200, 200, 200, 255, 200, 200, 200, 255, 7, 7, 7, 255 },
            decoded.Bgra);
    }

    [Fact]
    public void Stores_rows_bottom_to_top()
    {
        // 1×2 RGB, verbatim: the first stored row is the bottom one.
        var top = new byte[] { 0, 0, 255, 255 };    // red, BGRA
        var bottom = new byte[] { 255, 0, 0, 255 }; // blue
        var file = SgiImage.Encode([.. top, .. bottom], 1, 2, 3, compress: false);

        // Red plane begins right after the header: bottom (blue → 0) first, then top (red → 255).
        Assert.Equal(0, file[512]);
        Assert.Equal(255, file[513]);
    }

    [Theory]
    [InlineData(new byte[] { 1, 1, 1, 1, 1 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5 })]
    [InlineData(new byte[] { 1, 1, 2, 2, 3, 3, 3, 3, 4 })]
    public void Rle_rows_decode_to_what_was_encoded(byte[] row)
    {
        var bgra = row.SelectMany(v => new byte[] { v, v, v, 255 }).ToArray();
        var decoded = SgiImage.Decode(SgiImage.Encode(bgra, row.Length, 1, 1));
        Assert.Equal(bgra, decoded.Bgra);
    }

    [Fact]
    public void Long_runs_are_split()
    {
        var row = Enumerable.Repeat((byte)9, 300).ToArray();
        var encoded = SgiImage.EncodeRleRow(row);
        // 127 + 127 + 46, two bytes each, and the terminator.
        Assert.Equal(7, encoded.Length);
    }

    [Fact]
    public void Refuses_what_is_not_sgi() =>
        Assert.Throws<InvalidDataException>(() => SgiImage.Decode(new byte[600]));
}

public class ImageCodecTests
{
    /// <summary>A w×h bitmap whose every pixel encodes its own position.</summary>
    private static SKBitmap Numbered(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            bgra[i] = (byte)x;
            bgra[i + 1] = (byte)y;
            bgra[i + 2] = 99;
            bgra[i + 3] = 255;
        }
        return ImageCodec.FromBgra(bgra, width, height);
    }

    private static (int X, int Y) At(SKBitmap bitmap, int x, int y)
    {
        var bgra = ImageCodec.ToBgra(bitmap);
        var i = (y * bitmap.Width + x) * 4;
        return (bgra[i], bgra[i + 1]);
    }

    [Theory]
    [InlineData(0, 4, 2)]
    [InlineData(90, 2, 4)]
    [InlineData(180, 4, 2)]
    [InlineData(45, 5, 5)]
    public void Rotated_size_is_the_bounding_box(double degrees, int width, int height) =>
        Assert.Equal((width, height), ImageCodec.RotatedSize(4, 2, degrees));

    [Fact]
    public void A_quarter_turn_moves_pixels_exactly()
    {
        using var source = Numbered(4, 2);
        using var turned = ImageCodec.Rotate(source, 90, opaqueBackground: false);

        Assert.Equal(2, turned.Width);
        Assert.Equal(4, turned.Height);
        // Clockwise: the source's bottom-left corner becomes the top-left.
        Assert.Equal((0, 1), At(turned, 0, 0));
        Assert.Equal((0, 0), At(turned, 1, 0));
        Assert.Equal((3, 0), At(turned, 1, 3));
    }

    [Fact]
    public void A_half_turn_moves_pixels_exactly()
    {
        using var source = Numbered(4, 2);
        using var turned = ImageCodec.Rotate(source, 180, opaqueBackground: false);
        Assert.Equal((3, 1), At(turned, 0, 0));
        Assert.Equal((0, 0), At(turned, 3, 1));
    }

    [Fact]
    public void Three_quarter_turns_undo_one()
    {
        using var source = Numbered(5, 3);
        using var once = ImageCodec.Rotate(source, 90, false);
        using var back = ImageCodec.Rotate(once, 270, false);
        Assert.Equal(ImageCodec.ToBgra(source), ImageCodec.ToBgra(back));
    }

    [Fact]
    public void Other_angles_leave_transparent_corners()
    {
        using var source = Numbered(20, 20);
        using var turned = ImageCodec.Rotate(source, 45, opaqueBackground: false);
        var bgra = ImageCodec.ToBgra(turned);
        Assert.Equal(0, bgra[3]);
    }

    [Fact]
    public void Save_writes_the_rotation_and_reloads()
    {
        var folder = Directory.CreateTempSubdirectory("wlrix-imageview-test");
        try
        {
            foreach (var extension in new[] { "png", "rgb" })
            {
                var path = Path.Combine(folder.FullName, $"card.{extension}");
                using (var document = new ImageDocument(path, Numbered(4, 2), ImageFormat.Png, 3, 8, false))
                    ImageCodec.Save(document, 90, path, ImageFormats.FromPath(path));

                using var reloaded = ImageCodec.Load(path);
                Assert.Equal(2, reloaded.Width);
                Assert.Equal(4, reloaded.Height);
                Assert.Equal((0, 1), At(reloaded.Pixels, 0, 0));
                Assert.Empty(Directory.GetFiles(folder.FullName, "*.tmp"));
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Exif_origins_turn_the_pixels_upright()
    {
        using var source = Numbered(3, 2);
        using var rightTop = ImageCodec.Orient(source, SKEncodedOrigin.RightTop);
        using var flipped = ImageCodec.Orient(source, SKEncodedOrigin.TopRight);

        Assert.Equal((2, 3), (rightTop.Width, rightTop.Height));
        Assert.Equal((2, 0), At(flipped, 0, 0));
    }

    [Fact]
    public void Formats_come_from_the_extension()
    {
        Assert.Equal(ImageFormat.Jpeg, ImageFormats.FromPath("a/b.JPEG"));
        Assert.Equal(ImageFormat.Sgi, ImageFormats.FromPath("shot.rgb"));
        Assert.Equal(ImageFormat.Unknown, ImageFormats.FromPath("notes.txt"));
        Assert.True(ImageFormats.CanWrite(ImageFormat.Sgi));
        Assert.False(ImageFormats.CanWrite(ImageFormat.Gif));
    }
}

public class HistogramTests
{
    [Fact]
    public void Counts_each_channel_and_skips_transparent_pixels()
    {
        byte[] bgra =
        [
            0, 0, 255, 255,     // red
            0, 255, 0, 255,     // green
            255, 255, 255, 255, // white
            9, 9, 9, 0,         // invisible
        ];
        var histogram = Histogram.FromBgra(bgra, 16, 4, 1);

        Assert.Equal(3, histogram.PixelCount);
        Assert.Equal(2, histogram.Red[255]);
        Assert.Equal(1, histogram.Red[0]);
        Assert.Equal(2, histogram.Green[255]);
        Assert.Equal(2, histogram.Blue[0]);
        Assert.Equal(1, histogram.Luminance[255]);
        Assert.Equal(0, histogram.Red[9]);
    }

    [Fact]
    public void Stats_describe_the_selected_channel()
    {
        byte[] bgra = [0, 0, 10, 255, 0, 0, 20, 255, 0, 0, 30, 255, 0, 0, 100, 255];
        var stats = Histogram.FromBgra(bgra, 16, 4, 1).Stats(HistogramChannel.Red);

        Assert.Equal(10, stats.Min);
        Assert.Equal(100, stats.Max);
        Assert.Equal(40, stats.Mean, 6);
        Assert.Equal(20, stats.Median);
    }
}
