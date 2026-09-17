using System.Text;
using SkiaSharp;
using Wlrix.Files.Core.Thumbnails;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The shared thumbnail cache and the things that fill it.
/// </summary>
/// <remarks>
/// Two properties matter more than the rest and most of these are about them: the cache file
/// must be findable by other applications, which is the MD5 of a URI that has to agree with
/// GLib's; and a cached thumbnail must go stale when its source changes, which is entirely
/// carried by a PNG text chunk SkiaSharp cannot write.
/// </remarks>
public class ThumbnailTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A real encoded PNG of the given size, for splicing and decoding.</summary>
    private static byte[] SamplePng(int width = 4, int height = 4)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] SampleJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Firebrick);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    // --- the text chunks --------------------------------------------------

    [Fact]
    public void TextChunksSurviveASpliceAndComeBackOut()
    {
        // The whole invalidation story rests on this: SkiaSharp encodes the pixels and cannot
        // write a text chunk, so if the splice is wrong there is no mtime and no cached
        // thumbnail is ever considered stale.
        var written = PngText.Write(SamplePng(),
        [
            new(PngText.UriKey, "file:///tmp/a%20b.png"),
            new(PngText.MTimeKey, "1757700000"),
            new(PngText.SoftwareKey, ThumbnailCache.Software)
        ]);

        var read = PngText.Read(written);
        Assert.Equal("file:///tmp/a%20b.png", read[PngText.UriKey]);
        Assert.Equal("1757700000", read[PngText.MTimeKey]);
        Assert.Equal(ThumbnailCache.Software, read[PngText.SoftwareKey]);
    }

    [Fact]
    public void ASplicedPngStillDecodes()
    {
        // A chunk with a wrong length or CRC produces a file that some decoders still open,
        // which is the worst kind of wrong -- it would work here and fail in Nautilus.
        var written = PngText.Write(SamplePng(8, 6), [new(PngText.MTimeKey, "1")]);
        using var decoded = SKBitmap.Decode(written);

        Assert.NotNull(decoded);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(6, decoded.Height);
    }

    [Fact]
    public void TheChunkCrcIsOverTheTypeAndDataAndNotTheLength()
    {
        // Both values come from zlib rather than from this implementation. A CRC that is
        // merely self-consistent passes every round-trip test in this file and fails in every
        // other PNG reader on the machine, so agreeing with something else is the point.
        // 0xCBF43926 over "123456789" is the standard CRC-32 check value.
        Assert.Equal(0xCBF43926u, PngText.Crc32("123456789"u8));
        Assert.Equal(0x9642C585u, PngText.Crc32("tEXt"u8));
    }

    [Fact]
    public void ChunksGoInAfterTheHeaderRatherThanAtTheEnd()
    {
        var written = PngText.Write(SamplePng(), [new(PngText.MTimeKey, "1")]);
        var text = Encoding.Latin1.GetString(written);
        // Before the pixel data, so a reader finds the metadata without walking the image.
        Assert.True(text.IndexOf("tEXt", StringComparison.Ordinal)
                    < text.IndexOf("IDAT", StringComparison.Ordinal));
    }

    [Fact]
    public void SomethingThatIsNotAPngIsRefusedOnWriteAndIgnoredOnRead()
    {
        // Read runs over whatever is in a shared cache directory: another application's
        // half-written file, or something that is not an image at all.
        Assert.Throws<ArgumentException>(() => PngText.Write("not a png"u8, []));
        Assert.Empty(PngText.Read("not a png"u8));
        Assert.Empty(PngText.Read([]));
        Assert.Empty(PngText.Read(SamplePng().AsSpan(0, 20)));
    }

    [Fact]
    public void AKeywordTooLongForThePngSpecIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            PngText.Write(SamplePng(), [new(new string('k', 80), "x")]));
        Assert.Throws<ArgumentException>(() => PngText.Write(SamplePng(), [new(string.Empty, "x")]));
    }

    // --- the cache --------------------------------------------------------

    [Fact]
    public void TheCacheFileIsWhereTheSpecificationSaysItIs()
    {
        // The MD5 of the URI, in a directory named for the size. Any deviation makes the
        // cache private, which defeats sharing it with every other file manager.
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/a b.png");

        Assert.Equal("f2584ab78dd95a88bd0d3f0ecaee7a8c", ThumbnailCache.HashOf(location));
        Assert.Equal(Path.Combine(dir.Path, "normal", "f2584ab78dd95a88bd0d3f0ecaee7a8c.png"),
            cache.PathFor(location, ThumbnailSize.Normal));
        Assert.Equal(Path.Combine(dir.Path, "large", "f2584ab78dd95a88bd0d3f0ecaee7a8c.png"),
            cache.PathFor(location, ThumbnailSize.Large));
        Assert.Equal(Path.Combine(dir.Path, "fail", "wlrix-files", "f2584ab78dd95a88bd0d3f0ecaee7a8c.png"),
            cache.FailurePathFor(location));
    }

    [Fact]
    public void AStoredThumbnailComesBackAndCarriesTheMetadataTheSpecWants()
    {
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/photo.jpg");
        var modified = DateTimeOffset.FromUnixTimeSeconds(1_757_700_000);

        cache.Store(location, ThumbnailSize.Normal, SamplePng(), modified, 4096, "image/jpeg");

        var loaded = cache.TryLoad(location, ThumbnailSize.Normal, modified);
        Assert.NotNull(loaded);

        var text = PngText.Read(loaded);
        Assert.Equal(location.ToUriString(), text[PngText.UriKey]);
        Assert.Equal("1757700000", text[PngText.MTimeKey]);
        Assert.Equal("4096", text[PngText.SizeKey]);
        Assert.Equal("image/jpeg", text[PngText.MimeKey]);
    }

    [Fact]
    public void AThumbnailGoesStaleWhenItsSourceIsModified()
    {
        // The one thing the text chunks exist for. Without it every thumbnail is permanent
        // and editing a photograph never changes its preview.
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/photo.jpg");
        var before = DateTimeOffset.FromUnixTimeSeconds(1_757_700_000);

        cache.Store(location, ThumbnailSize.Normal, SamplePng(), before, 4096, "image/jpeg");

        Assert.NotNull(cache.TryLoad(location, ThumbnailSize.Normal, before));
        Assert.Null(cache.TryLoad(location, ThumbnailSize.Normal, before.AddSeconds(1)));
    }

    [Fact]
    public void ValidityIsWholeSecondsBecauseThatIsWhatAUnixMtimeIs()
    {
        // Comparing with more precision than the file stores would make every thumbnail
        // stale on the very next look.
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/photo.jpg");
        var modified = DateTimeOffset.FromUnixTimeSeconds(1_757_700_000);

        cache.Store(location, ThumbnailSize.Normal, SamplePng(), modified, 1, "image/jpeg");
        Assert.NotNull(cache.TryLoad(location, ThumbnailSize.Normal, modified.AddMilliseconds(999)));
    }

    [Fact]
    public void TheCacheIsPrivateToTheUser()
    {
        // A thumbnail is a picture of the user's file. A world-readable cache of those would
        // disclose content the file's own permissions may not.
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/photo.jpg");
        cache.Store(location, ThumbnailSize.Normal, SamplePng(), DateTimeOffset.UnixEpoch, 1, "image/png");

        var path = cache.PathFor(location, ThumbnailSize.Normal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void NothingPartialIsLeftInTheCacheDirectory()
    {
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        cache.Store(Location.FromLocalPath("/tmp/a.png"), ThumbnailSize.Normal,
            SamplePng(), DateTimeOffset.UnixEpoch, 1, "image/png");

        Assert.Empty(Directory.GetFiles(dir.Path, "*.wlrix-new", SearchOption.AllDirectories));
    }

    [Fact]
    public void AFailureMarkerSuppressesRetriesUntilTheFileChanges()
    {
        // Otherwise a broken or enormous file is re-attempted on every scroll past it, and a
        // directory of them never settles.
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/broken.jpg");
        var modified = DateTimeOffset.FromUnixTimeSeconds(1_757_700_000);

        Assert.False(cache.HasFailed(location, modified));
        cache.StoreFailure(location, ThumbnailProviderMarker, modified);

        Assert.True(cache.HasFailed(location, modified));
        // ...and replacing the file with a good one clears it, rather than being suppressed
        // for ever by a failure that no longer applies.
        Assert.False(cache.HasFailed(location, modified.AddSeconds(1)));
    }

    private static byte[] ThumbnailProviderMarker => SamplePng(1, 1);

    [Fact]
    public void AnUnreadableCacheIsAMissRatherThanAFailure()
    {
        using var dir = new TemporaryDirectory();
        var cache = new ThumbnailCache(dir.Path);
        var location = Location.FromLocalPath("/tmp/a.png");
        var path = cache.PathFor(location, ThumbnailSize.Normal);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a png");

        Assert.Null(cache.TryLoad(location, ThumbnailSize.Normal, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(128, ThumbnailSize.Normal)]
    [InlineData(48, ThumbnailSize.Normal)]
    [InlineData(129, ThumbnailSize.Large)]
    [InlineData(256, ThumbnailSize.Large)]
    [InlineData(512, ThumbnailSize.XLarge)]
    [InlineData(1024, ThumbnailSize.XXLarge)]
    public void ARequestedSizeRoundsUpToAStandardOne(int pixels, ThumbnailSize expected) =>
        Assert.Equal(expected, ThumbnailCache.SizeFor(pixels));

    // --- rendering --------------------------------------------------------

    [Theory]
    [InlineData(1000, 500, 128, 128, 64)]
    [InlineData(500, 1000, 128, 64, 128)]
    [InlineData(400, 400, 128, 128, 128)]
    [InlineData(64, 32, 128, 64, 32)]
    public void AThumbnailFitsTheSquareAndKeepsItsShape(
        int width, int height, int size, int expectedWidth, int expectedHeight)
    {
        // Never enlarged: a 64-pixel image blown up to 128 is a blurry lie, and the spec
        // allows a thumbnail to be smaller than the nominal size.
        Assert.Equal((expectedWidth, expectedHeight), SkiaImageThumbnailer.Fit(width, height, size));
    }

    [Fact]
    public async Task ARasterImageIsThumbnailedDownToTheRequestedSize()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "big.jpg");
        File.WriteAllBytes(path, SampleJpeg(800, 400));

        var png = await new SkiaImageThumbnailer().RenderAsync(path, 128, None);
        Assert.NotNull(png);

        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(128, decoded.Width);
        Assert.Equal(64, decoded.Height);
    }

    [Fact]
    public async Task AnSvgIsRasterizedRatherThanDecodedAndScaled()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "vector.svg");
        File.WriteAllText(path,
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="red"/></svg>""");

        var png = await new SvgThumbnailer().RenderAsync(path, 64, None);
        Assert.NotNull(png);
        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(64, decoded.Width);
    }

    [Fact]
    public async Task SomethingThatIsNotAnImageRendersNothingRatherThanThrowing()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "notanimage.png");
        File.WriteAllText(path, "this is text pretending to be a png");

        Assert.Null(await new SkiaImageThumbnailer().RenderAsync(path, 128, None));
    }

    [Fact]
    public void EachThumbnailerClaimsOnlyWhatItCanDo()
    {
        var raster = new SkiaImageThumbnailer();
        var vector = new SvgThumbnailer();

        Assert.True(raster.CanHandle("image/jpeg"));
        Assert.True(raster.CanHandle("image/png"));
        Assert.False(raster.CanHandle("image/svg+xml"));
        Assert.False(raster.CanHandle("text/plain"));

        Assert.True(vector.CanHandle("image/svg+xml"));
        Assert.False(vector.CanHandle("image/png"));

        Assert.True(CompositeThumbnailer.Default.CanHandle("image/svg+xml"));
        Assert.True(CompositeThumbnailer.Default.CanHandle("image/webp"));
        Assert.False(CompositeThumbnailer.Default.CanHandle("application/pdf"));

        // ...and the composite answers for the whole set, so the size limit follows whichever
        // thumbnailer would actually be used.
        Assert.True(CompositeThumbnailer.Default.ReadsWholeFile("image/png"));
    }

    [Fact]
    public void WritingAKeyTheImageAlreadyHadReplacesItRatherThanAddingASecond()
    {
        // The case that appeared the moment a thumbnail came from somebody else's program:
        // ffmpegthumbnailer writes its own Thumb::MTime, Thumb::Size, Thumb::Mimetype and a
        // Thumb::URI holding a bare path rather than a URI. Appending after those leaves two
        // answers to every question, and a reader taking the last one gets theirs.
        var once = PngText.Write(SamplePng(), [
            new(PngText.UriKey, "/not/a/uri.mp4"),
            new("Thumb::Movie::Length", "5")
        ]);

        var twice = PngText.Write(once, [
            new(PngText.UriKey, "file:///a/uri.mp4"),
            new(PngText.SoftwareKey, "wlRIX Files")
        ]);

        var read = PngText.Read(twice);
        Assert.Equal("file:///a/uri.mp4", read[PngText.UriKey]);
        Assert.Equal("wlRIX Files", read[PngText.SoftwareKey]);

        // A keyword nobody is replacing is left exactly where it was.
        Assert.Equal("5", read["Thumb::Movie::Length"]);

        // And there is genuinely one of each, not merely one that reads back.
        Assert.Equal(1, Occurrences(twice, PngText.UriKey));
        Assert.Equal(1, Occurrences(twice, "Thumb::Movie::Length"));
    }

    /// <summary>How many tEXt chunks carry a keyword, counted in the bytes.</summary>
    private static int Occurrences(byte[] png, string keyword)
    {
        var needle = System.Text.Encoding.Latin1.GetBytes(keyword + "\0");
        var count = 0;
        for (var i = 0; i + needle.Length <= png.Length; i++)
        {
            if (png.AsSpan(i, needle.Length).SequenceEqual(needle))
                count++;
        }

        return count;
    }
}
