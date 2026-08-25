using System.Text;
using Wlrix.Archiver.Services.Encodings;
using Xunit;

namespace Wlrix.Archiver.Tests;

public class FilenameDecoderTests
{
    public FilenameDecoderTests() => EncodingCatalog.Register();

    [Fact]
    public void Utf8NamesAreDecodedAsUtf8WithoutBeingGuessedAt()
    {
        var decoder = new FilenameDecoder();

        var text = decoder.Decode(Encoding.UTF8.GetBytes("文書/読みこみ.txt"));

        Assert.Equal("文書/読みこみ.txt", text);
        // Nothing was guessed, so nothing is remembered — the next name in the same archive is
        // still judged on its own bytes.
        Assert.Null(decoder.Detected);
    }

    [Fact]
    public void ShiftJisNamesAreDetectedRatherThanTurnedIntoMojibake()
    {
        var decoder = new FilenameDecoder();

        var text = decoder.Decode(Encoding.GetEncoding(932).GetBytes("読みこみ.txt"));

        Assert.Equal("読みこみ.txt", text);
        Assert.Equal("cp932", decoder.Detected?.Id);
    }

    [Fact]
    public void AsciiNamesAreLeftAloneBecauseEveryCodePageAgreesAboutThem()
    {
        var decoder = new FilenameDecoder();

        Assert.Equal("src/main.c", decoder.Decode("src/main.c"u8));
        Assert.Null(decoder.Detected);
    }

    [Fact]
    public void TheDetectedEncodingSticksSoOneListingStaysInternallyConsistent()
    {
        var decoder = new FilenameDecoder();
        var sjis = Encoding.GetEncoding(932);

        decoder.Decode(sjis.GetBytes("読みこみ.txt"));
        var detected = decoder.Detected;
        // A short, ambiguous name that in isolation might score better as something else. It
        // must follow the archive's first answer rather than start a second one.
        decoder.Decode(sjis.GetBytes("あ.txt"));

        Assert.Same(detected, decoder.Detected);
    }

    [Fact]
    public void ChoosingAnEncodingForcesItEvenOverBytesThatAreValidUtf8()
    {
        var decoder = new FilenameDecoder
        {
            Selected = EncodingCatalog.ById("cp437"),
        };

        // Valid UTF-8 for "é", but the user said CP437, so it decodes as the two box-drawing
        // characters CP437 makes of those bytes. Overriding has to actually override, or the
        // menu item does nothing on exactly the archives someone would reach for it.
        var text = decoder.Decode(Encoding.UTF8.GetBytes("é.txt"));

        Assert.Equal("├⌐.txt", text);
    }

    [Fact]
    public void ChoosingAnEncodingClearsWhateverDetectionHadSettledOn()
    {
        var decoder = new FilenameDecoder();
        decoder.Decode(Encoding.GetEncoding(932).GetBytes("読みこみ.txt"));
        Assert.NotNull(decoder.Detected);

        decoder.Selected = EncodingCatalog.ById("cp936");

        Assert.Null(decoder.Detected);
    }

    [Fact]
    public void ResetForgetsTheDetectionSoTheNextArchiveIsJudgedOnItsOwn()
    {
        var decoder = new FilenameDecoder();
        decoder.Decode(Encoding.GetEncoding(932).GetBytes("読みこみ.txt"));

        decoder.Reset();

        Assert.Null(decoder.Detected);
    }

    [Theory]
    // The CJK pages, each against a name that only makes sense in its own script.
    [InlineData(932, "日本語のファイル.txt", "cp932")]
    [InlineData(936, "中文文件名.txt", "cp936")]
    [InlineData(949, "한국어파일.txt", "cp949")]
    public void EachCjkCodePageIsRecognizedFromItsOwnScript(int codePage, string name,
        string expected)
    {
        var bytes = Encoding.GetEncoding(codePage).GetBytes(name);

        Assert.Equal(expected, FilenameDecoder.Guess(bytes).Id);
    }

    [Fact]
    public void ScoringPrefersRealScriptOverTheBoxDrawingASingleByteCodePageWouldFind()
    {
        var bytes = Encoding.GetEncoding(932).GetBytes("日本語");

        // The point of the scorer. CP437 decodes these bytes without error — every single-byte
        // page decodes everything — so only plausibility separates the two, and box-drawing
        // characters are what a wrong single-byte guess produces.
        var asCp437 = Encoding.GetEncoding(437).GetString(bytes);
        var asCp932 = Encoding.GetEncoding(932).GetString(bytes);

        Assert.True(FilenameDecoder.Score(asCp932) > FilenameDecoder.Score(asCp437));
    }

    [Fact]
    public void ScoringPrefersOneCoherentScriptOverAnAlternatingJumble()
    {
        // The failure the coherence penalty exists for. A Shift-JIS name read as CP1251 comes
        // out as real Cyrillic letters interleaved with ASCII — per character it scores as well
        // as the Japanese it should have been, and only the alternation gives it away.
        var bytes = Encoding.GetEncoding(932).GetBytes("日本語のファイル.txt");

        var asCp932 = Encoding.GetEncoding(932).GetString(bytes);
        var asCp1251 = Encoding.GetEncoding(1251).GetString(bytes);

        Assert.True(FilenameDecoder.Score(asCp932) > FilenameDecoder.Score(asCp1251));
    }

    [Fact]
    public void KanaIsWhatSeparatesJapaneseFromChineseWhenBothDecodeCleanly()
    {
        // Han ideographs are shared by CP932, CP936 and CP950, so they barely discriminate; a
        // Japanese name read as GBK is a plausible-looking string of ideographs. The kana is
        // the part only one of the two can produce.
        var bytes = Encoding.GetEncoding(932).GetBytes("日本語のファイル.txt");

        Assert.True(FilenameDecoder.Score(Encoding.GetEncoding(932).GetString(bytes))
            > FilenameDecoder.Score(Encoding.GetEncoding(936).GetString(bytes)));
    }

    [Fact]
    public void InvalidUtf8IsRecognizedAsSuchRatherThanSilentlyReplaced()
    {
        // A lone continuation byte: valid CP932 lead-trail material, never valid UTF-8.
        Assert.False(FilenameDecoder.IsValidUtf8([0x93, 0xc7]));
        Assert.True(FilenameDecoder.IsValidUtf8("ok"u8));
    }

    [Fact]
    public void AnEmptyNameDecodesToAnEmptyStringRatherThanThrowing()
    {
        Assert.Equal(string.Empty, new FilenameDecoder().Decode([]));
    }
}
