// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.ClipboardHistory.Models;
using Xunit;

namespace Wlrix.ClipboardHistory.Tests;

public class ImageInfoTests
{
    /// <summary>The first 24 bytes of a PNG that says it is <paramref name="width"/> by <paramref name="height"/>.</summary>
    private static byte[] Header(int width, int height)
    {
        byte[] header =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 0, 0, 0, 0, 0,
        ];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), height);
        return header;
    }

    [Fact]
    public void A_png_says_its_size_in_its_header()
    {
        Assert.Equal((3840, 2160), ImageInfo.PngSize(Header(3840, 2160)));
    }

    [Fact]
    public void Anything_else_has_no_size()
    {
        var jpeg = new byte[24];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        Assert.Null(ImageInfo.PngSize(jpeg));
        Assert.Null(ImageInfo.PngSize(Header(1, 1)[..20]));
    }

    [Fact]
    public void A_file_that_is_not_there_has_no_size()
    {
        Assert.Null(ImageInfo.PngSize("/nonexistent/clipboard/blob.png"));
    }
}
