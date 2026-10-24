// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Common.Imaging;
using Xunit;

namespace Wlrix.Common.Tests;

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
