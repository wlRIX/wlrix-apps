// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace Wlrix.ClipboardHistory.Models;

/// <summary>Reads an image's size without decoding it.</summary>
internal static class ImageInfo
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The width and height a PNG declares, or null for anything that is not one.
    /// </summary>
    /// <remarks>
    /// The rows show a thumbnail, which is decoded small; the caption wants the real size, and
    /// decoding a 4K screenshot whole to learn it would cost a hundred megabytes for one number.
    /// A PNG says it in its first chunk, at a fixed offset. Nearly every image on a clipboard is
    /// a PNG, so the rare other one just goes without a size.
    /// </remarks>
    internal static (int Width, int Height)? PngSize(ReadOnlySpan<byte> header)
    {
        // Signature, then IHDR's length and type, then width and height, big-endian.
        if (header.Length < 24 || !header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary><see cref="PngSize(ReadOnlySpan{byte})"/> for a file, or null when it cannot be read.</summary>
    internal static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                ? PngSize(header)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
