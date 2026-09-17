using Wlrix.Files.Core.Thumbnails;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The other half of the Thumbnail Managing Standard: the programs the system installs to make
/// a thumbnail, and the <c>.thumbnailer</c> files that declare them.
/// </summary>
public class ExternalThumbnailerTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static string Write(TemporaryDirectory temp, string name, string contents)
    {
        var directory = Path.Combine(temp.Path, "thumbnailers");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    // --- reading the entries -------------------------------------------------

    [Fact]
    public void AThumbnailerFileIsRead()
    {
        using var temp = new TemporaryDirectory();
        var path = Write(temp, "video.thumbnailer", """
            [Thumbnailer Entry]
            TryExec=ffmpegthumbnailer
            Exec=ffmpegthumbnailer -i %i -o %o -s %s -f
            MimeType=video/mp4;video/webm;
            """);

        var entry = ExternalThumbnailer.Parse(path);

        Assert.NotNull(entry);
        Assert.Equal("ffmpegthumbnailer", entry!.TryExec);
        Assert.Equal(["video/mp4", "video/webm"], entry.MimeTypes.Order().ToArray());
    }

    [Fact]
    public void AFileWithNoExecOrNoTypesIsNotAThumbnailer()
    {
        using var temp = new TemporaryDirectory();
        Assert.Null(ExternalThumbnailer.Parse(Write(temp, "a.thumbnailer", """
            [Thumbnailer Entry]
            MimeType=video/mp4;
            """)));
        Assert.Null(ExternalThumbnailer.Parse(Write(temp, "b.thumbnailer", """
            [Thumbnailer Entry]
            Exec=something %i %o
            """)));
    }

    [Fact]
    public void OnlyTheThumbnailerGroupIsRead()
    {
        // Desktop-entry syntax, so a file can carry other groups. Reading a key out of the
        // wrong one would run whatever another group happened to name.
        using var temp = new TemporaryDirectory();
        var path = Write(temp, "c.thumbnailer", """
            [Desktop Entry]
            Exec=the-wrong-program %i %o
            MimeType=text/plain;

            [Thumbnailer Entry]
            Exec=the-right-program %i %o
            MimeType=video/mp4;
            """);

        var entry = ExternalThumbnailer.Parse(path);
        Assert.StartsWith("the-right-program", entry!.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("text/plain", entry.MimeTypes);
    }

    [Fact]
    public void AnEntryWhoseProgramIsGoneIsNotUsed()
    {
        // The file is dropped in a shared directory and nothing removes it when the package
        // goes, so this outlives its program often enough to be worth checking once rather
        // than failing per file.
        using var temp = new TemporaryDirectory();
        Write(temp, "ghost.thumbnailer", """
            [Thumbnailer Entry]
            TryExec=a-program-that-is-not-installed-anywhere
            Exec=a-program-that-is-not-installed-anywhere %i %o
            MimeType=video/x-ghost;
            """);

        var entries = ExternalThumbnailer.Discover([Path.Combine(temp.Path, "thumbnailers")]);
        Assert.DoesNotContain(entries, entry => entry.MimeTypes.Contains("video/x-ghost"));
    }

    [Fact]
    public void TheFirstDirectoryToClaimATypeKeepsIt()
    {
        // What lets somebody override a system thumbnailer from their own data directory.
        using var mine = new TemporaryDirectory();
        using var system = new TemporaryDirectory();
        Write(mine, "a.thumbnailer", """
            [Thumbnailer Entry]
            Exec=mine %i %o
            MimeType=video/mp4;
            """);
        Write(system, "a.thumbnailer", """
            [Thumbnailer Entry]
            Exec=theirs %i %o
            MimeType=video/mp4;video/webm;
            """);

        var entries = ExternalThumbnailer.Discover(
            [Path.Combine(mine.Path, "thumbnailers"), Path.Combine(system.Path, "thumbnailers")]);

        var mp4 = entries.Single(entry => entry.MimeTypes.Contains("video/mp4"));
        Assert.StartsWith("mine", mp4.Command, StringComparison.Ordinal);

        // ...and the loser keeps the types the winner did not claim.
        var webm = entries.Single(entry => entry.MimeTypes.Contains("video/webm"));
        Assert.StartsWith("theirs", webm.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfHasABuiltInEntryBecauseTheSystemUsuallyShipsNone()
    {
        // GNOME's PDF thumbnailer lives inside Evince, so a machine with poppler and no Evince
        // can render a PDF and has nothing declaring that it can.
        var builtin = Assert.Single(ExternalThumbnailer.Builtins);
        Assert.Contains("application/pdf", builtin.MimeTypes);
        Assert.Equal("pdftoppm", builtin.TryExec);
    }

    // --- the command line ----------------------------------------------------

    [Fact]
    public void TheFieldCodesAreFilledIn()
    {
        var arguments = ExternalThumbnailer.Substitute(
            "ffmpegthumbnailer -i %i -o %o -s %s", "/tmp/a video.mp4", "/tmp/out.png", 256);

        Assert.Equal(
            ["ffmpegthumbnailer", "-i", "/tmp/a video.mp4", "-o", "/tmp/out.png", "-s", "256"],
            arguments);
    }

    [Fact]
    public void TheUriFormIsOfferedToo()
    {
        var arguments = ExternalThumbnailer.Substitute(
            "glycin-thumbnailer --input %u --output %o", "/tmp/a video.mp4", "/tmp/out.png", 256);

        Assert.Contains("file:///tmp/a%20video.mp4", arguments);
    }

    [Fact]
    public void AFilenameIsAnArgumentAndNeverPartOfACommandLine()
    {
        // "; rm -rf ~" is a legal filename. It stays one argument because there is no shell
        // here for it to mean anything to, and the split happens before substitution.
        var arguments = ExternalThumbnailer.Substitute(
            "thumb %i %o", "/tmp/; rm -rf ~/notes.mp4", "/tmp/out.png", 128);

        Assert.Equal(["thumb", "/tmp/; rm -rf ~/notes.mp4", "/tmp/out.png"], arguments);
    }

    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("a \"b c\" d", new[] { "a", "b c", "d" })]
    [InlineData("a 'b c'", new[] { "a", "b c" })]
    [InlineData("   spaced   out   ", new[] { "spaced", "out" })]
    public void ACommandLineSplitsOnWhitespaceOutsideQuotes(string command, string[] expected) =>
        Assert.Equal(expected, ExternalThumbnailer.Split(command));

    // --- the size limit ------------------------------------------------------

    [Fact]
    public void AProgramThatSeeksIsNotSubjectToTheImageSizedLimit()
    {
        // The limit exists because Skia decodes the whole file. ffmpegthumbnailer takes one
        // frame out of a film and pdftoppm renders one page, and neither cares whether the
        // file is four megabytes or forty gigabytes. Applying the cap to those would mean no
        // video over it ever got a preview, which is most of them.
        var entry = new ThumbnailerEntry("cp %i %o", "cp", new HashSet<string> { "video/mp4" }, "test");

        // Through the interface: a default interface member is not visible on the concrete
        // type, which is a C# rule worth tripping over once rather than twice.
        Assert.False(((IThumbnailer)new ExternalThumbnailer([entry])).ReadsWholeFile);
        Assert.True(((IThumbnailer)new SkiaImageThumbnailer()).ReadsWholeFile);
        Assert.True(((IThumbnailer)new SvgThumbnailer()).ReadsWholeFile);
    }

    // --- running it ----------------------------------------------------------

    [Fact]
    public async Task AThumbnailerThatWritesAPngIsBelieved()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "movie.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);

        // cp is a thumbnailer that copies its input, which is enough to test the plumbing:
        // the point here is the process, the output file and the bytes coming back.
        var payload = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
        File.WriteAllBytes(source, payload);

        var entry = new ThumbnailerEntry(
            "cp %i %o", "cp", new HashSet<string> { "video/mp4" }, "test");
        var thumbnailer = new ExternalThumbnailer([entry]);

        Assert.True(thumbnailer.CanHandle("video/mp4"));
        Assert.Equal(payload, await thumbnailer.RenderAsync(source, "video/mp4", 128, None));
    }

    [Fact]
    public async Task AProgramThatAppendsAnExtensionIsStillFound()
    {
        // poppler's pdftoppm appends ".png" to whatever name it is given, with or without
        // -singlefile and even when the name already ends in .png. Spec-compliant thumbnailers
        // write exactly where they are told, so only the built-in entries declare this.
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "paper.pdf");
        File.WriteAllText(source, "x");

        // A stand-in for pdftoppm: writes to the name plus an extension, as it does.
        var script = Path.Combine(temp.Path, "appender");
        await File.WriteAllTextAsync(script, "#!/bin/sh\ncp \"$1\" \"$2.png\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);

        var entry = new ThumbnailerEntry(
            $"{script} %i %o", script, new HashSet<string> { "application/pdf" }, "test", OutputSuffix: ".png");

        var before = Directory.GetFiles(Path.GetTempPath(), "wlrix-thumb-*").Length;
        Assert.NotNull(await new ExternalThumbnailer([entry]).RenderAsync(source, "application/pdf", 128, None));

        // ...and nothing is left behind under the name the program actually wrote, which is
        // not the name we chose. Counted rather than globbed, because a stale file from
        // somewhere else would otherwise fail this — as one did, left by the version of this
        // code that deleted the wrong name.
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "wlrix-thumb-*").Length);
    }

    [Fact]
    public void OnlyTheBuiltInEntriesDeclareAnOutputSuffix()
    {
        // An installed .thumbnailer has no way to say this and none needs to: the spec says
        // the program writes to %o. Reading one must never produce a suffix.
        using var temp = new TemporaryDirectory();
        var path = Write(temp, "x.thumbnailer", """
            [Thumbnailer Entry]
            Exec=whatever %i %o
            MimeType=video/mp4;
            """);

        Assert.Equal(string.Empty, ExternalThumbnailer.Parse(path)!.OutputSuffix);
        Assert.Equal(".png", ExternalThumbnailer.Builtins.Single().OutputSuffix);
    }

    [Fact]
    public async Task AThumbnailerThatWritesNothingIsNotBelieved()
    {
        // Exits zero, produces no file. Answering "here is a thumbnail" would cache an empty
        // one forever.
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "movie.mp4");
        File.WriteAllText(source, "x");

        var entry = new ThumbnailerEntry("true", "true", new HashSet<string> { "video/mp4" }, "test");
        Assert.Null(await new ExternalThumbnailer([entry]).RenderAsync(source, "video/mp4", 128, None));
    }

    [Fact]
    public async Task AThumbnailerThatFailsIsNotBelievedEitherWayRound()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "movie.mp4");
        File.WriteAllText(source, "x");

        var entry = new ThumbnailerEntry("false", "false", new HashSet<string> { "video/mp4" }, "test");
        Assert.Null(await new ExternalThumbnailer([entry]).RenderAsync(source, "video/mp4", 128, None));
    }

    [Fact]
    public async Task AThumbnailerThatHangsIsKilled()
    {
        // ffmpegthumbnailer on a truncated video is the case, and without a deadline it holds
        // a queue slot forever.
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "movie.mp4");
        File.WriteAllText(source, "x");

        var entry = new ThumbnailerEntry("sleep 30", "sleep", new HashSet<string> { "video/mp4" }, "test");
        var thumbnailer = new ExternalThumbnailer([entry], TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        Assert.Null(await thumbnailer.RenderAsync(source, "video/mp4", 128, None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ATypeNothingClaimsIsNotRendered()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "notes.txt");
        File.WriteAllText(source, "x");

        var entry = new ThumbnailerEntry("cp %i %o", "cp", new HashSet<string> { "video/mp4" }, "test");
        var thumbnailer = new ExternalThumbnailer([entry]);

        Assert.False(thumbnailer.CanHandle("text/plain"));
        Assert.Null(await thumbnailer.RenderAsync(source, "text/plain", 128, None));
    }

    [Fact]
    public async Task NothingIsLeftBehindInTheTempDirectory()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, "movie.mp4");
        File.WriteAllText(source, "x");

        var before = Directory.GetFiles(Path.GetTempPath(), "wlrix-thumb-*.png").Length;
        var entry = new ThumbnailerEntry("cp %i %o", "cp", new HashSet<string> { "video/mp4" }, "test");
        await new ExternalThumbnailer([entry]).RenderAsync(source, "video/mp4", 128, None);

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "wlrix-thumb-*.png").Length);
    }
}
