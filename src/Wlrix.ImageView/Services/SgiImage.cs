// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Wlrix.ImageView.Services;

/// <summary>A decoded SGI image: top-down, unpremultiplied BGRA rows.</summary>
/// <param name="Bgra">Pixels, four bytes each, <paramref name="Width"/> × 4 bytes per row.</param>
/// <param name="Channels">What the file stored: 1 gray, 2 gray and alpha, 3 RGB, 4 RGBA.</param>
/// <param name="BitsPerChannel">8 or 16. Sixteen-bit files are reduced to eight here.</param>
/// <param name="Compressed">Whether the file was run-length encoded.</param>
public sealed record SgiDecoded(byte[] Bgra, int Width, int Height, int Channels,
    int BitsPerChannel, bool Compressed);

/// <summary>Reads and writes the SGI image format, IRIX's native one (<c>.rgb</c>, <c>.bw</c>).</summary>
/// <remarks>
/// <para>Written by hand because Skia has no codec for it, and the format is small. It has a
/// 512-byte big-endian header, then one plane per channel, each made of scanlines stored
/// bottom to top. Scanlines are either verbatim or run-length encoded. An RLE file has a
/// table of offsets and lengths, indexed by <c>row + channel × height</c>, so the scanlines
/// can be in any order and can even share bytes.</para>
///
/// <para>The reader takes 1 to 4 channels at 8 or 16 bits. It does not read the old colormap
/// variants (dithered, screen and colormap files), which only IRIX's own tools wrote and
/// which have not been seen in decades. The writer always writes 8 bits per channel, RLE,
/// with as few channels as the pixels need.</para>
/// </remarks>
public static class SgiImage
{
    public const ushort Magic = 474;
    private const int HeaderSize = 512;
    private const int MaxRun = 127;

    /// <summary>Whether <paramref name="header"/> starts the way an SGI file does.</summary>
    public static bool IsSgi(ReadOnlySpan<byte> header) =>
        header.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(header) == Magic;

    public static SgiDecoded Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || !IsSgi(data))
            throw new InvalidDataException("Not an SGI image.");

        var storage = data[2];
        var bpc = data[3];
        var dimension = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        int height = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int depth = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        var pixMax = BinaryPrimitives.ReadInt32BigEndian(data[16..]);
        var colormap = BinaryPrimitives.ReadInt32BigEndian(data[104..]);

        if (storage > 1 || bpc is not (1 or 2))
            throw new InvalidDataException("Unsupported SGI storage or depth.");
        if (colormap != 0)
            throw new InvalidDataException("SGI colormap images are not supported.");

        // A one-dimensional image is a single row, a two-dimensional one is a single channel;
        // the header's other sizes are not to be trusted for those.
        if (dimension == 1)
            height = 1;
        if (dimension <= 2)
            depth = 1;
        if (width == 0 || height == 0 || depth == 0)
            throw new InvalidDataException("SGI image has no pixels.");

        // A file may carry more than four channels. Only the first four mean anything here.
        var channels = Math.Min(depth, 4);
        var planes = new byte[channels][];
        for (var z = 0; z < channels; z++)
            planes[z] = new byte[width * height];

        // Sixteen-bit samples are reduced to their high byte, unless the file says its values
        // never exceed 255, in which case they were eight-bit values stored wide.
        var narrow = bpc == 2 && pixMax is > 0 and <= 255;
        var row = new ushort[width];

        for (var z = 0; z < channels; z++)
        {
            for (var y = 0; y < height; y++)
            {
                if (storage == 0)
                    ReadVerbatimRow(data, HeaderSize + (z * height + y) * width * bpc, bpc, row);
                else
                    ReadRleRow(data, RleOffset(data, y + z * height), bpc, row);

                var plane = planes[z].AsSpan((height - 1 - y) * width, width);
                for (var x = 0; x < width; x++)
                    plane[x] = bpc == 1 ? (byte)row[x] : narrow ? (byte)row[x] : (byte)(row[x] >> 8);
            }
        }

        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            byte r, g, b, a = 255;
            switch (channels)
            {
                case 1:
                    r = g = b = planes[0][i];
                    break;
                case 2:
                    r = g = b = planes[0][i];
                    a = planes[1][i];
                    break;
                default:
                    r = planes[0][i];
                    g = planes[1][i];
                    b = planes[2][i];
                    if (channels == 4)
                        a = planes[3][i];
                    break;
            }

            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = a;
        }

        return new SgiDecoded(bgra, width, height, channels, bpc * 8, storage == 1);
    }

    private static void ReadVerbatimRow(ReadOnlySpan<byte> data, int offset, int bpc, ushort[] row)
    {
        var length = row.Length * bpc;
        if (offset < 0 || offset + length > data.Length)
            throw new InvalidDataException("SGI image is truncated.");

        var source = data.Slice(offset, length);
        for (var x = 0; x < row.Length; x++)
            row[x] = bpc == 1 ? source[x] : BinaryPrimitives.ReadUInt16BigEndian(source[(x * 2)..]);
    }

    private static int RleOffset(ReadOnlySpan<byte> data, int index)
    {
        var at = HeaderSize + index * 4;
        if (at + 4 > data.Length)
            throw new InvalidDataException("SGI offset table is truncated.");
        var offset = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        return offset > int.MaxValue ? -1 : (int)offset;
    }

    private static void ReadRleRow(ReadOnlySpan<byte> data, int offset, int bpc, ushort[] row)
    {
        if (offset < HeaderSize || offset >= data.Length)
            throw new InvalidDataException("SGI scanline offset is out of range.");

        var at = offset;
        var x = 0;

        ushort Next(ReadOnlySpan<byte> source)
        {
            if (at + bpc > source.Length)
                throw new InvalidDataException("SGI scanline is truncated.");
            var value = bpc == 1 ? source[at] : BinaryPrimitives.ReadUInt16BigEndian(source[at..]);
            at += bpc;
            return value;
        }

        while (true)
        {
            var packet = Next(data);
            var count = packet & 0x7f;
            if (count == 0)
                break;

            // A row that runs past its width is a broken file, but the extra pixels are
            // dropped rather than refused: older writers were not always careful about it.
            if ((packet & 0x80) != 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var value = Next(data);
                    if (x < row.Length)
                        row[x++] = value;
                }
            }
            else
            {
                var value = Next(data);
                for (var i = 0; i < count && x < row.Length; i++)
                    row[x++] = value;
            }
        }

        // A short row leaves the rest black instead of whatever the previous row held.
        Array.Clear(row, x, row.Length - x);
    }

    /// <summary>
    /// The fewest channels that hold <paramref name="bgra"/> without loss: gray when every
    /// pixel is neutral, and alpha only when some pixel is not fully opaque.
    /// </summary>
    public static int ChooseChannels(ReadOnlySpan<byte> bgra)
    {
        bool gray = true, alpha = false;
        for (var i = 0; i < bgra.Length && (gray || !alpha); i += 4)
        {
            if (bgra[i] != bgra[i + 1] || bgra[i + 1] != bgra[i + 2])
                gray = false;
            if (bgra[i + 3] != 255)
                alpha = true;
        }

        return (gray ? 1 : 3) + (alpha ? 1 : 0);
    }

    /// <summary>Encodes top-down, unpremultiplied BGRA pixels as an 8-bit SGI file.</summary>
    /// <param name="channels">1 to 4. Gray channels are taken as luma when the pixels are not neutral.</param>
    /// <param name="compress">RLE when true, verbatim otherwise.</param>
    /// <param name="name">Stored in the header's 79-character name field.</param>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int channels,
        bool compress = true, string name = "")
    {
        if (channels is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), "SGI images are at most 65535 pixels on a side.");

        // Planes in file order: rows bottom to top.
        var planes = new byte[channels][];
        for (var z = 0; z < channels; z++)
            planes[z] = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var source = (height - 1 - y) * width * 4;
            for (var x = 0; x < width; x++)
            {
                var p = source + x * 4;
                int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2], a = bgra[p + 3];
                var i = y * width + x;
                switch (channels)
                {
                    case 1:
                    case 2:
                        planes[0][i] = (byte)Models.Histogram.Luma(r, g, b);
                        if (channels == 2)
                            planes[1][i] = (byte)a;
                        break;
                    default:
                        planes[0][i] = (byte)r;
                        planes[1][i] = (byte)g;
                        planes[2][i] = (byte)b;
                        if (channels == 4)
                            planes[3][i] = (byte)a;
                        break;
                }
            }
        }

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header, Magic);
        header[2] = compress ? (byte)1 : (byte)0;
        header[3] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], channels == 1 ? (ushort)2 : (ushort)3);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)channels);
        BinaryPrimitives.WriteInt32BigEndian(header[12..], 0);
        BinaryPrimitives.WriteInt32BigEndian(header[16..], 255);
        var nameBytes = Encoding.ASCII.GetBytes(name);
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 79)).CopyTo(header[24..]);
        output.Write(header);

        if (!compress)
        {
            foreach (var plane in planes)
                output.Write(plane);
            return output.ToArray();
        }

        var rows = height * channels;
        var encoded = new byte[rows][];
        for (var z = 0; z < channels; z++)
            for (var y = 0; y < height; y++)
                encoded[y + z * height] = EncodeRleRow(planes[z].AsSpan(y * width, width));

        // The offset table, then the length table, then the rows in table order.
        var offset = HeaderSize + rows * 8;
        Span<byte> word = stackalloc byte[4];
        foreach (var row in encoded)
        {
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)offset);
            output.Write(word);
            offset += row.Length;
        }
        foreach (var row in encoded)
        {
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)row.Length);
            output.Write(word);
        }
        foreach (var row in encoded)
            output.Write(row);

        return output.ToArray();
    }

    /// <summary>One scanline as RLE packets, ending with the zero packet.</summary>
    /// <remarks>
    /// A repeat packet is written for three or more equal bytes. Two equal bytes cost the
    /// same either way, so they stay inside a literal packet and do not break it in two.
    /// </remarks>
    internal static byte[] EncodeRleRow(ReadOnlySpan<byte> row)
    {
        var output = new List<byte>(row.Length + row.Length / MaxRun + 2);
        var i = 0;

        bool RunStartsAt(ReadOnlySpan<byte> source, int at) =>
            at + 2 < source.Length && source[at] == source[at + 1] && source[at] == source[at + 2];

        while (i < row.Length)
        {
            if (RunStartsAt(row, i))
            {
                var value = row[i];
                var run = 1;
                while (i + run < row.Length && row[i + run] == value && run < MaxRun)
                    run++;
                output.Add((byte)run);
                output.Add(value);
                i += run;
            }
            else
            {
                var start = i;
                do
                    i++;
                while (i < row.Length && i - start < MaxRun && !RunStartsAt(row, i));

                output.Add((byte)(0x80 | (i - start)));
                for (var k = start; k < i; k++)
                    output.Add(row[k]);
            }
        }

        output.Add(0);
        return output.ToArray();
    }
}
