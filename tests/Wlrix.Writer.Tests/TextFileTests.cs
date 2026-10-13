// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Wlrix.Writer.Models;
using Xunit;

namespace Wlrix.Writer.Tests;

public class TextFileTests
{
    [Fact]
    public void Plain_utf8_decodes_without_a_bom()
    {
        var decoded = TextFile.Decode(Encoding.UTF8.GetBytes("héllo\nwörld\n"));

        Assert.Equal("héllo\nwörld\n", decoded.Text);
        Assert.Equal(65001, decoded.Format.Encoding.CodePage);
        Assert.False(decoded.Format.HasBom);
        Assert.Equal(LineEnding.Lf, decoded.Format.LineEnding);
    }

    [Fact]
    public void A_utf8_bom_is_remembered_and_written_back()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("text\n")];

        var decoded = TextFile.Decode(bytes);
        var (encoded, format) = TextFile.Encode(decoded.Text, decoded.Format);

        Assert.Equal("text\n", decoded.Text);
        Assert.True(decoded.Format.HasBom);
        Assert.Equal(bytes, encoded);
        Assert.Equal(decoded.Format, format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16_with_a_bom_round_trips(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("日本語\r\ntext\r\n")];

        var decoded = TextFile.Decode(bytes);
        var (encoded, _) = TextFile.Encode(decoded.Text, decoded.Format);

        Assert.Equal("日本語\ntext\n", decoded.Text);
        Assert.Equal(LineEnding.CrLf, decoded.Format.LineEnding);
        Assert.Equal(bytes, encoded);
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_latin1_and_saves_back_byte_for_byte()
    {
        // "café" in Latin-1: the é is 0xE9, which is not valid UTF-8 on its own.
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9, 0x0A];

        var decoded = TextFile.Decode(bytes);
        var (encoded, _) = TextFile.Encode(decoded.Text, decoded.Format);

        Assert.Equal("café\n", decoded.Text);
        Assert.Equal(28591, decoded.Format.Encoding.CodePage);
        Assert.Equal(bytes, encoded);
    }

    [Fact]
    public void A_character_the_encoding_cannot_hold_widens_the_save_to_utf8()
    {
        var latin1 = new TextFormat(Encoding.Latin1, false, LineEnding.Lf);

        var (encoded, format) = TextFile.Encode("price: 5€\n", latin1);

        Assert.Equal(65001, format.Encoding.CodePage);
        Assert.Equal("price: 5€\n", Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void Crlf_files_are_edited_as_lf_and_saved_as_crlf()
    {
        var decoded = TextFile.Decode(Encoding.ASCII.GetBytes("one\r\ntwo\r\n"));

        // A line typed in the editor arrives as a bare LF, and must come out CRLF like its neighbors.
        var (encoded, _) = TextFile.Encode(decoded.Text + "three\n", decoded.Format);

        Assert.Equal("one\ntwo\n", decoded.Text);
        Assert.Equal(LineEnding.CrLf, decoded.Format.LineEnding);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void Old_mac_line_endings_survive()
    {
        var decoded = TextFile.Decode(Encoding.ASCII.GetBytes("a\rb\r"));
        var (encoded, _) = TextFile.Encode(decoded.Text, decoded.Format);

        Assert.Equal(LineEnding.Cr, decoded.Format.LineEnding);
        Assert.Equal("a\rb\r", Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void A_file_without_line_breaks_is_lf()
    {
        Assert.Equal(LineEnding.Lf, TextFile.Decode("single line"u8).Format.LineEnding);
        Assert.Equal(LineEnding.Lf, TextFile.Decode([]).Format.LineEnding);
    }

    [Fact]
    public void A_nul_early_on_marks_a_file_as_binary()
    {
        byte[] elf = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];

        Assert.Throws<BinaryFileException>(() => TextFile.Decode(elf));
    }

    [Fact]
    public void A_nul_past_the_probe_is_left_alone()
    {
        var bytes = new byte[TextFile.BinaryProbeLength + 10];
        Array.Fill(bytes, (byte)'a');
        bytes[^1] = 0;

        Assert.Equal(bytes.Length, TextFile.Decode(bytes).Text.Length);
    }
}
