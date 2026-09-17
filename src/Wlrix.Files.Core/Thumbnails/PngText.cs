using System.Buffers.Binary;
using System.Text;

namespace Wlrix.Files.Core.Thumbnails;

/// <summary>
/// Reading and writing the <c>tEXt</c> chunks a thumbnail carries.
/// </summary>
/// <remarks>
/// The thumbnail specification is built on them: a cached thumbnail is valid only if its
/// <c>Thumb::MTime</c> still matches the source file, so without these a cache can never be
/// invalidated and every stale thumbnail is permanent. SkiaSharp encodes PNGs and offers no
/// way to add a text chunk, so this splices them in afterwards.
///
/// <para>
/// A PNG is a signature followed by length-prefixed chunks, each ending in a CRC-32 over its
/// type and data. Inserting one is therefore a matter of building the bytes and putting them
/// somewhere legal — immediately after <c>IHDR</c>, which must be the first chunk — rather
/// than re-encoding anything.
/// </para>
/// </remarks>
public static class PngText
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The keys the thumbnail specification defines.</summary>
    public const string UriKey = "Thumb::URI";
    public const string MTimeKey = "Thumb::MTime";
    public const string SizeKey = "Thumb::Size";
    public const string MimeKey = "Thumb::Mimetype";
    public const string SoftwareKey = "Software";

    /// <summary>
    /// Returns <paramref name="png"/> with the given text chunks written after IHDR, replacing
    /// any it already had under the same keywords.
    /// </summary>
    /// <remarks>
    /// Replacing, not merely adding, and that matters as soon as the PNG did not come from
    /// here. <c>ffmpegthumbnailer</c> writes its own <c>Thumb::MTime</c>, <c>Thumb::Size</c>,
    /// <c>Thumb::Mimetype</c> and a <c>Thumb::URI</c> holding a bare path rather than a URI;
    /// appending ours after them leaves the file carrying two answers to every question, and a
    /// reader that takes the last one gets theirs. Keywords we are not writing — its
    /// <c>Thumb::Movie::Length</c>, say — are left exactly where they were.
    /// </remarks>
    /// <exception cref="ArgumentException">The bytes are not a PNG.</exception>
    public static byte[] Write(ReadOnlySpan<byte> png, IReadOnlyList<KeyValuePair<string, string>> text)
    {
        if (!png.StartsWith(Signature))
            throw new ArgumentException("not a PNG", nameof(png));

        // Straight after IHDR. The spec requires IHDR first and allows tEXt anywhere after it,
        // and putting them at the front means a reader finds them without scanning the pixels.
        var insertAt = Signature.Length + 4 + 4 + BinaryPrimitives.ReadInt32BigEndian(png[Signature.Length..]) + 4;
        if (insertAt > png.Length)
            throw new ArgumentException("truncated PNG header", nameof(png));

        var replacing = new HashSet<string>(text.Select(static pair => pair.Key), StringComparer.Ordinal);
        var body = Without(png[insertAt..], replacing);

        var chunks = new List<byte[]>(text.Count);
        var extra = 0;
        foreach (var (key, value) in text)
        {
            var chunk = Chunk(key, value);
            chunks.Add(chunk);
            extra += chunk.Length;
        }

        var result = new byte[insertAt + extra + body.Length];
        png[..insertAt].CopyTo(result);
        var at = insertAt;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result.AsSpan(at));
            at += chunk.Length;
        }
        body.CopyTo(result.AsSpan(at));
        return result;
    }

    /// <summary>
    /// Everything after IHDR except the <c>tEXt</c> chunks whose keyword is being replaced.
    /// </summary>
    /// <remarks>
    /// Copied chunk by chunk rather than edited in place, because removing bytes from the
    /// middle of a stream of length-prefixed records is exactly the operation that goes wrong
    /// quietly. A chunk whose length runs past the end stops the walk and the rest is taken
    /// verbatim, which keeps a malformed file merely unedited rather than corrupted.
    /// </remarks>
    private static byte[] Without(ReadOnlySpan<byte> body, HashSet<string> keywords)
    {
        var kept = new List<byte>(body.Length);
        var at = 0;

        while (at + 12 <= body.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(body[at..]);
            if (length < 0 || at + 12 + length > body.Length)
                break;

            var type = Latin1.GetString(body.Slice(at + 4, 4));
            var total = 12 + length;

            if (type == "tEXt")
            {
                var data = body.Slice(at + 8, length);
                var nul = data.IndexOf((byte)0);
                var keyword = Latin1.GetString(nul < 0 ? data : data[..nul]);
                if (keywords.Contains(keyword))
                {
                    at += total;
                    continue;
                }
            }

            kept.AddRange(body.Slice(at, total));
            at += total;
        }

        if (at < body.Length)
            kept.AddRange(body[at..]);

        return [.. kept];
    }

    /// <summary>Reads every <c>tEXt</c> chunk out of a PNG.</summary>
    /// <remarks>
    /// Tolerant by design: a file that is not a PNG, or is truncated, answers an empty set
    /// rather than throwing. This runs against whatever is in the cache directory, which may
    /// have been written by another application, half-written by one that crashed, or not be
    /// an image at all.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Read(ReadOnlySpan<byte> png)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!png.StartsWith(Signature))
            return found;

        var at = Signature.Length;
        while (at + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[at..]);
            if (length < 0 || at + 12 + length > png.Length)
                break;

            var type = Encoding.ASCII.GetString(png.Slice(at + 4, 4));
            if (type == "tEXt")
            {
                var data = png.Slice(at + 8, length);
                var separator = data.IndexOf((byte)0);
                if (separator > 0)
                {
                    // Latin-1, per the PNG specification. Everything the thumbnail spec puts
                    // in one is ASCII after percent-encoding, so this is exact rather than
                    // lossy, but decoding as UTF-8 would mangle a chunk another application
                    // wrote with a high byte in it.
                    found[Latin1.GetString(data[..separator])] = Latin1.GetString(data[(separator + 1)..]);
                }
            }

            if (type == "IEND")
                break;
            at += 12 + length;
        }
        return found;
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static byte[] Chunk(string key, string value)
    {
        var keyBytes = Latin1.GetBytes(key);
        var valueBytes = Latin1.GetBytes(value);
        if (keyBytes.Length is 0 or > 79)
            throw new ArgumentException($"a PNG text keyword must be 1 to 79 bytes: '{key}'", nameof(key));

        var data = new byte[keyBytes.Length + 1 + valueBytes.Length];
        keyBytes.CopyTo(data, 0);
        data[keyBytes.Length] = 0;
        valueBytes.CopyTo(data, keyBytes.Length + 1);

        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk.AsSpan(8));
        // Over the type and the data, not the length. Getting that wrong produces a file that
        // most decoders still open, which is the worst kind of wrong.
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    /// <summary>The CRC-32 a PNG chunk ends with.</summary>
    /// <remarks>
    /// Hand-rolled rather than pulled from <c>System.IO.Hashing</c>: it is fifteen lines, it
    /// is the only hash this assembly needs beyond MD5, and it keeps a package reference off
    /// the library whose dependency list is deliberately short.
    /// </remarks>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
