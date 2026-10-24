// SPDX-License-Identifier: GPL-3.0-or-later

using SkiaSharp;
using Wlrix.ImageView.Models;
using Wlrix.ImageView.Services;
using Xunit;

namespace Wlrix.ImageView.Tests;

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
                    ImageCodec.Save(document, new ImageState(ViewTransform.Identity.WithRotation(90), Enhancement.Identity),
                        path, ImageFormats.FromPath(path));

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

public class EnhancementTests
{
    /// <summary>Applies the color matrix to one 0–255 color, the way Skia would.</summary>
    private static (double R, double G, double B) Apply(Enhancement enhancement, double r, double g, double b)
    {
        var m = enhancement.ColorMatrix();
        double[] c = [r / 255, g / 255, b / 255];
        double Row(int i) => (m[i * 5] * c[0] + m[i * 5 + 1] * c[1] + m[i * 5 + 2] * c[2] + m[i * 5 + 4]) * 255;
        return (Row(0), Row(1), Row(2));
    }

    [Fact]
    public void Identity_changes_nothing()
    {
        var (r, g, b) = Apply(Enhancement.Identity, 10, 128, 250);
        Assert.Equal(10, r, 3);
        Assert.Equal(128, g, 3);
        Assert.Equal(250, b, 3);
        Assert.True(Enhancement.Identity.IsIdentity);
        Assert.False(Enhancement.Identity.ChangesColor);
    }

    [Fact]
    public void Brightness_adds()
    {
        var (r, _, _) = Apply(Enhancement.Identity with { Brightness = 0.5 }, 0, 0, 0);
        Assert.Equal(127.5, r, 3);
    }

    [Fact]
    public void Contrast_scales_about_mid_gray()
    {
        var doubled = Enhancement.Identity with { Contrast = 1 };
        Assert.Equal(127.5, Apply(doubled, 127.5, 0, 0).R, 3);
        Assert.Equal(-127.5, Apply(doubled, 0, 0, 0).R, 3);
        // At -1 everything is mid-gray.
        Assert.Equal(127.5, Apply(Enhancement.Identity with { Contrast = -1 }, 255, 0, 0).R, 3);
    }

    [Fact]
    public void Haze_sets_a_black_level_or_lifts_toward_white()
    {
        var removed = Enhancement.Identity with { Haze = 0.5 };
        Assert.Equal(0, Apply(removed, 127.5, 0, 0).R, 3);
        Assert.Equal(255, Apply(removed, 255, 0, 0).R, 3);

        var added = Enhancement.Identity with { Haze = -0.5 };
        Assert.Equal(127.5, Apply(added, 0, 0, 0).R, 3);
        Assert.Equal(255, Apply(added, 255, 0, 0).R, 3);
    }

    [Fact]
    public void Saturation_zero_is_gray_and_negative_inverts_the_hue()
    {
        var gray = Apply(Enhancement.Identity with { Saturation = 0 }, 255, 0, 0);
        Assert.Equal(gray.R, gray.G, 3);
        Assert.Equal(gray.G, gray.B, 3);
        Assert.Equal(0.299 * 255, gray.R, 2);

        var inverted = Apply(Enhancement.Identity with { Saturation = -1 }, 255, 0, 0);
        Assert.True(inverted.R < inverted.G);
    }

    [Fact]
    public void Settings_are_clamped_to_their_ranges()
    {
        var wild = new Enhancement(99, -99, 5, 9, -9).Clamped();
        Assert.Equal(new Enhancement(Enhancement.MaxSharpness, Enhancement.MinBrightness, Enhancement.MaxHaze,
            Enhancement.MaxContrast, Enhancement.MinSaturation), wild);
    }

    private static SKBitmap Solid(byte r, byte g, byte b, int size = 8)
    {
        var bgra = new byte[size * size * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return ImageCodec.FromBgra(bgra, size, size);
    }

    [Fact]
    public void The_processor_applies_the_colors()
    {
        using var black = Solid(0, 0, 0);
        using var brighter = ImageProcessor.Enhance(black, Enhancement.Identity with { Brightness = 0.5 });
        var pixel = brighter.GetPixel(4, 4);
        Assert.InRange(pixel.Red, 126, 129);
        Assert.Equal(255, pixel.Alpha);
    }

    [Fact]
    public void Sharpening_a_flat_image_changes_nothing()
    {
        using var flat = Solid(90, 120, 200);
        using var sharpened = ImageProcessor.Enhance(flat, Enhancement.Identity with { Sharpness = 3 });
        using var blurred = ImageProcessor.Enhance(flat, Enhancement.Identity with { Sharpness = -3 });
        // Edges included: the blur clamps rather than reading transparent black from outside.
        Assert.Equal(flat.GetPixel(0, 0), sharpened.GetPixel(0, 0));
        Assert.Equal(flat.GetPixel(0, 0), blurred.GetPixel(0, 0));
        Assert.Equal(flat.GetPixel(4, 4), sharpened.GetPixel(4, 4));
    }

    [Fact]
    public void Sharpening_steepens_an_edge()
    {
        // Left half dark, right half light.
        var bgra = new byte[16 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 16; x++)
        {
            var v = (byte)(x < 8 ? 80 : 160);
            var i = (y * 16 + x) * 4;
            bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
            bgra[i + 3] = 255;
        }
        using var edge = ImageCodec.FromBgra(bgra, 16, 4);
        using var sharpened = ImageProcessor.Enhance(edge, Enhancement.Identity with { Sharpness = 2 });
        Assert.True(sharpened.GetPixel(7, 2).Red < 80);
        Assert.True(sharpened.GetPixel(8, 2).Red > 160);
    }

    [Fact]
    public void Save_renders_flips_then_rotation()
    {
        // Pixel (x, y) holds x in blue. A horizontal flip puts x = 2 at the left; a quarter turn
        // clockwise then moves the old bottom-left corner to the top-left.
        var bgra = new byte[3 * 2 * 4];
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 3; x++)
        {
            var i = (y * 3 + x) * 4;
            bgra[i] = (byte)x;
            bgra[i + 1] = (byte)y;
            bgra[i + 3] = 255;
        }
        using var source = ImageCodec.FromBgra(bgra, 3, 2);
        var state = new ImageState(ViewTransform.Identity.WithFlips(true, false).WithRotation(90), Enhancement.Identity);

        using var rendered = ImageCodec.Render(source, state, opaqueBackground: false);

        Assert.Equal((2, 3), (rendered.Width, rendered.Height));
        var corner = rendered.GetPixel(0, 0);
        Assert.Equal((2, 1), ((int)corner.Blue, (int)corner.Green));
    }
}
