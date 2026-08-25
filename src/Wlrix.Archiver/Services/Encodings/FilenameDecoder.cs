using System.Text;
using SharpCompress.Common;

namespace Wlrix.Archiver.Services.Encodings;

/// <summary>
/// Turns the raw bytes an archive stores for a filename into text.
/// </summary>
/// <remarks>
/// zip and tar both predate Unicode and neither is obliged to say what encoding a name is in.
/// Zip added a flag (general-purpose bit 11) that marks a name as UTF-8, and SharpCompress
/// honors it before ever reaching this class — but the archives that actually cause trouble are
/// the ones written by tools that never set it: a Shift-JIS zip from a Japanese Windows box, a
/// GBK one from a Chinese one, a CP437 one from DOS. Decoding those as UTF-8 or as Latin-1
/// produces mojibake, and mojibake in a filename is not cosmetic — it is the name the file gets
/// written out under.
///
/// So: if the bytes are valid UTF-8 they are UTF-8, because no legacy code page produces valid
/// multi-byte UTF-8 by accident at any length. Otherwise the candidates are scored and the best
/// one wins. The user can override the answer from <c>View / Encoding</c>, which is the escape
/// hatch for the cases no heuristic gets right.
/// </remarks>
public sealed class FilenameDecoder
{
    /// <summary>UTF-8 that throws rather than substituting U+FFFD, for use as a validity test.</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private FilenameEncoding _selected = EncodingCatalog.Automatic;

    public FilenameDecoder() => EncodingCatalog.Register();

    /// <summary>
    /// What the user picked. <see cref="EncodingCatalog.Automatic"/> means detect per archive.
    /// </summary>
    public FilenameEncoding Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Detected = null;
        }
    }

    /// <summary>
    /// What detection settled on while reading the current archive, or <c>null</c> if nothing
    /// needed detecting (every name was valid UTF-8) or a fixed encoding is in force.
    /// </summary>
    /// <remarks>
    /// Sticky for the life of an archive on purpose. Names are short, and a single one is often
    /// too little evidence to tell CP932 from CP936; deciding once from the first name that
    /// needs it and applying that to the rest keeps one listing internally consistent, which
    /// half-and-half mojibake would not be.
    /// </remarks>
    public FilenameEncoding? Detected { get; private set; }

    /// <summary>Forgets the sticky detection. Call when opening a different archive.</summary>
    public void Reset() => Detected = null;

    /// <summary>
    /// The decoder to hand SharpCompress as <c>ArchiveEncoding.CustomDecoder</c>.
    /// </summary>
    /// <remarks>
    /// The <see cref="EncodingType"/> argument is SharpCompress saying the format already knows
    /// the answer — a zip with bit 11 set reports <see cref="EncodingType.UTF8"/> — and it is
    /// respected as-is. Second-guessing a declaration in favor of a guess would be strictly
    /// worse than not guessing at all.
    /// </remarks>
    public Func<byte[], int, int, EncodingType, string> CustomDecoder =>
        (bytes, index, count, type) => type == EncodingType.UTF8
            ? Encoding.UTF8.GetString(bytes, index, count)
            : Decode(bytes.AsSpan(index, count));

    /// <summary>Decodes one name.</summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        if (!Selected.IsAutomatic)
            return DecodeWith(Selected.Resolve()!, bytes);

        if (IsValidUtf8(bytes))
            return Encoding.UTF8.GetString(bytes);

        Detected ??= Guess(bytes);
        return DecodeWith(Detected.Resolve() ?? Encoding.UTF8, bytes);
    }

    /// <summary>Whether <paramref name="bytes"/> decodes as UTF-8 without a single bad sequence.</summary>
    internal static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The best-scoring candidate for bytes that are not UTF-8.</summary>
    internal static FilenameEncoding Guess(ReadOnlySpan<byte> bytes)
    {
        var best = EncodingCatalog.All[1];
        var bestScore = int.MinValue;

        foreach (var candidate in EncodingCatalog.All)
        {
            if (candidate.IsAutomatic || candidate.CodePage == 65001)
                continue;

            string text;
            try
            {
                // Strict, so a code page with undefined bytes (CP1252 has five) disqualifies
                // itself rather than scoring on a replacement character.
                var encoding = Encoding.GetEncoding(candidate.CodePage,
                    EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                text = encoding.GetString(bytes);
            }
            catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException
                                           or NotSupportedException)
            {
                continue;
            }

            var score = Score(text);
            // Strictly greater, so the catalog order breaks ties — which puts the CJK pages
            // ahead of the single-byte ones that can decode literally anything.
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// How much a decoded name looks like a real filename rather than line noise.
    /// </summary>
    /// <remarks>
    /// Every single-byte code page decodes every byte sequence without error, so validity
    /// cannot separate a right answer from a wrong one and only plausibility can.
    ///
    /// Two things are scored. Per character, script letters count for more than ASCII and the
    /// characters nobody puts in a filename count against — box drawing especially, since that
    /// is what CP437 makes of CJK. But the per-character score alone is not enough: a Shift-JIS
    /// name read as CP1251 comes out as a *string of Cyrillic letters*, which scores as well as
    /// the Japanese it should have been.
    ///
    /// What actually separates them is coherence. A real name is written in one script, or in
    /// one script plus ASCII; mojibake alternates every character or two, because the lead and
    /// trail bytes of each multi-byte character land in different ranges of the wrong code
    /// page. So each change of script costs, and that is what sinks the near-miss candidates.
    /// </remarks>
    internal static int Score(string text)
    {
        var score = 0;
        var previous = Script.Neutral;

        foreach (var c in text)
        {
            score += CharacterScore(c);

            var script = ScriptOf(c);
            if (script == Script.Neutral)
                continue;

            // Compared against the last script *that had one*, so an intervening symbol does
            // not hide an alternation.
            if (previous != Script.Neutral && script != previous)
                score -= 3;

            previous = script;
        }

        return score;
    }

    private static int CharacterScore(char c) => c switch
    {
        // Controls have no business in a filename at all, and a stray one is the strongest
        // available signal that the code page is wrong.
        < ' ' or '\u007f' => -8,
        <= '~' => 1,
        '\ufffd' => -8,
        _ => NonAsciiScore(c),
    };

    private static int NonAsciiScore(char c) => c switch
    {
        // Kana scores above everything else: Han is shared by CP932, CP936 and CP950 and so
        // barely discriminates between them, whereas kana appears in almost every Japanese
        // filename and in no Chinese one. Hangul is *not* given the same weight even though it
        // is equally exclusive to CP949, because Shift-JIS bytes read as CP949 come out as a
        // run of Hangul syllables — scoring those at 3 let the wrong answer win.
        >= '\u3040' and <= '\u30ff' => 3,
        >= '\u3400' and <= '\u9fff' => 2,
        >= '\uf900' and <= '\ufaff' => 2,
        >= '\uac00' and <= '\ud7af' => 2,
        // Half-width and full-width forms, common in CJK filenames.
        >= '\uff00' and <= '\uffef' => 1,
        // Core Russian Cyrillic and Greek, and the accented Latin letters. All worth the same
        // as an ASCII letter and no more, which is the correction that makes this work at all:
        // a single-byte code page turns N bytes of CJK into N letters where the right answer
        // produces N/2, so anything scoring a Cyrillic letter as highly as a Han character
        // hands the win to the mojibake on volume alone.
        //
        // The range is deliberately *not* the whole Cyrillic block: U+0400-U+040F and
        // U+0450-U+045F are the extended letters a misread Shift-JIS name lands in, and they
        // score nothing.
        >= '\u0391' and <= '\u03c9' => 1,
        '\u0401' or '\u0451' => 1,
        >= '\u0410' and <= '\u044f' => 1,
        >= '\u00c0' and <= '\u024f' => 1,
        // Box drawing, block elements, geometric shapes. CP437's answer to anything multi-byte,
        // and the clearest possible tell that a single-byte page is being fed CJK.
        >= '\u2500' and <= '\u25ff' => -10,
        // Private use: nothing legitimate decodes here.
        >= '\ue000' and <= '\uf8ff' => -8,
        // Unpaired surrogates, and the noncharacters at the end of the BMP.
        >= '\ud800' and <= '\udfff' => -8,
        >= '\ufff0' => -8,
        // Everything else — symbols, currency, punctuation — is neither evidence nor noise.
        _ => 0,
    };

    /// <summary>Which writing system a character belongs to, for the coherence penalty.</summary>
    private enum Script
    {
        /// <summary>Symbols and punctuation: carries no signal, so it breaks no run.</summary>
        Neutral,
        Ascii,
        /// <summary>Kana and Han together. Mixing them is ordinary Japanese, not mojibake.</summary>
        Cjk,
        Hangul,
        Cyrillic,
        Greek,
        Latin,
    }

    private static Script ScriptOf(char c) => c switch
    {
        // Digits and punctuation are neutral; a name is not "in ASCII" because it has a dot in
        // it, and counting one would make every extension look like a script change.
        >= 'a' and <= 'z' or >= 'A' and <= 'Z' => Script.Ascii,
        <= '~' => Script.Neutral,
        >= '\u3040' and <= '\u30ff' => Script.Cjk,
        >= '\u3400' and <= '\u9fff' => Script.Cjk,
        >= '\uf900' and <= '\ufaff' => Script.Cjk,
        >= '\uac00' and <= '\ud7af' => Script.Hangul,
        >= '\u0400' and <= '\u04ff' => Script.Cyrillic,
        >= '\u0370' and <= '\u03ff' => Script.Greek,
        >= '\u00c0' and <= '\u024f' => Script.Latin,
        _ => Script.Neutral,
    };

    /// <summary>Decodes with a replacement fallback: a wrong guess must not throw.</summary>
    private static string DecodeWith(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
