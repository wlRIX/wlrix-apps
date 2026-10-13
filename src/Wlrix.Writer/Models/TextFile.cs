// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Wlrix.Writer.Models;

/// <summary>Which characters end a line in a file.</summary>
public enum LineEnding
{
    Lf,
    CrLf,
    Cr,
}

/// <summary>How a file was written, so that saving it writes it back the same way.</summary>
/// <param name="Encoding">The character encoding, without a preamble of its own.</param>
/// <param name="HasBom">Whether the file began with a byte order mark.</param>
/// <param name="LineEnding">What the file's lines ended with.</param>
public sealed record TextFormat(Encoding Encoding, bool HasBom, LineEnding LineEnding)
{
    /// <summary>What a new document is saved as: UTF-8, no BOM, LF.</summary>
    public static TextFormat Default { get; } = new(TextFile.Utf8, false, LineEnding.Lf);
}

/// <summary>A file's bytes turned into editable text.</summary>
/// <param name="Text">The text, with every line ending turned into <c>\n</c>.</param>
public sealed record DecodedText(string Text, TextFormat Format);

/// <summary>Raised for a file that is not text.</summary>
public sealed class BinaryFileException() : IOException("The file is not a text file.");

/// <summary>Decoding a file into text and encoding it back.</summary>
/// <remarks>
/// <para>
/// The editor holds every line ending as <c>\n</c>, whatever the file had, and puts the file's
/// own ending back on save. That is what keeps a CRLF file CRLF when somebody presses Enter in
/// it: the buffer never sees a <c>\r</c> to copy, so the new line cannot come out different
/// from its neighbors.
/// </para>
/// <para>
/// Encodings: a BOM is believed, then strict UTF-8 is tried, and anything that is not valid UTF-8
/// is read as Latin-1. Latin-1 maps every byte to a character, so a file in some other 8-bit
/// encoding still opens and saves back byte for byte, which matters more than displaying it
/// right.
/// </para>
/// </remarks>
public static class TextFile
{
    /// <summary>How far into a file a NUL byte marks it as binary.</summary>
    public const int BinaryProbeLength = 8192;

    /// <summary>UTF-8 that throws on invalid input instead of inventing U+FFFD.</summary>
    internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false,
        throwOnInvalidCharacters: true);

    /// <summary>Turns a file's bytes into text.</summary>
    /// <exception cref="BinaryFileException">The bytes are not text.</exception>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes)
    {
        string text;
        Encoding encoding;
        var hasBom = true;

        // UTF-32 LE before UTF-16 LE: its BOM begins with UTF-16's.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            (encoding, text) = (Utf32Le, DecodeOrThrow(Utf32Le, bytes[4..]));
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            (encoding, text) = (Utf8, DecodeOrThrow(Utf8, bytes[3..]));
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            (encoding, text) = (Utf16Le, DecodeOrThrow(Utf16Le, bytes[2..]));
        else if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            (encoding, text) = (Utf16Be, DecodeOrThrow(Utf16Be, bytes[2..]));
        else
        {
            hasBom = false;

            // A NUL is never part of text in an 8-bit or UTF-8 file, and every binary format has
            // them early. UTF-16 without a BOM has them too, and is rare enough on Linux to take
            // that trade.
            if (bytes[..Math.Min(bytes.Length, BinaryProbeLength)].Contains((byte)0))
                throw new BinaryFileException();

            try
            {
                (encoding, text) = (Utf8, Utf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                (encoding, text) = (Encoding.Latin1, Encoding.Latin1.GetString(bytes));
            }
        }

        var ending = DetectLineEnding(text);
        return new DecodedText(NormalizeLineEndings(text), new TextFormat(encoding, hasBom, ending));
    }

    /// <summary>Turns text back into the bytes of a file written in <paramref name="format"/>.</summary>
    /// <returns>
    /// The bytes, and the format they are actually in. That is <paramref name="format"/> unless
    /// the text holds a character the encoding cannot write (a euro sign typed into a Latin-1
    /// file), in which case it is UTF-8 rather than a file with question marks in it.
    /// </returns>
    public static (byte[] Bytes, TextFormat Format) Encode(string text, TextFormat format)
    {
        var withEndings = ApplyLineEnding(text, format.LineEnding);
        var strict = Strict(format.Encoding);
        try
        {
            return (Assemble(strict, format.HasBom, withEndings), format);
        }
        catch (EncoderFallbackException)
        {
            var widened = format with { Encoding = Utf8, HasBom = false };
            return (Assemble(Utf8, false, withEndings), widened);
        }
    }

    /// <summary>The ending of the first line break, or LF for a file with none.</summary>
    public static LineEnding DetectLineEnding(string text)
    {
        var index = text.AsSpan().IndexOfAny('\r', '\n');
        if (index < 0 || text[index] == '\n')
            return LineEnding.Lf;
        return index + 1 < text.Length && text[index + 1] == '\n' ? LineEnding.CrLf : LineEnding.Cr;
    }

    /// <summary>Every CRLF and lone CR as LF.</summary>
    public static string NormalizeLineEndings(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private static string ApplyLineEnding(string text, LineEnding ending)
    {
        // The editor's buffer is LF already, but normalizing first means nothing pasted past it
        // can come out as a CR CR LF.
        var normalized = NormalizeLineEndings(text);
        return ending switch
        {
            LineEnding.CrLf => normalized.Replace("\n", "\r\n"),
            LineEnding.Cr => normalized.Replace('\n', '\r'),
            _ => normalized,
        };
    }

    private static string DecodeOrThrow(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // A BOM in front of bytes that are not that encoding: the file is not what it says.
            throw new BinaryFileException();
        }
    }

    private static byte[] Assemble(Encoding encoding, bool bom, string text)
    {
        var preamble = bom ? Preamble(encoding) : [];
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0)
            return body;

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>The BOM for an encoding, which every encoding here was built without.</summary>
    private static byte[] Preamble(Encoding encoding) => encoding.CodePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        _ => [],
    };

    /// <summary>The encoding with an exception fallback, so an unencodable character is an error.</summary>
    private static Encoding Strict(Encoding encoding) =>
        encoding.EncoderFallback is EncoderExceptionFallback
            ? encoding
            : Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
