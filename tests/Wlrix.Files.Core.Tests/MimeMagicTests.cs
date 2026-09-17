using System.Buffers.Binary;
using Wlrix.Files.Core.Mime;
using Xunit;

namespace Wlrix.Files.Core.Tests;

/// <summary>
/// The compiled <c>magic</c> reader.
/// </summary>
/// <remarks>
/// The format is binary and undocumented outside the specification's appendix, so these tests
/// build their own files with <see cref="Builder"/> rather than shipping an opaque fixture:
/// the bytes under test are then visible beside the assertion. The integration tests below use
/// the shared <c>Fixtures/mime/magic</c>, which is generated in the same shape.
/// </remarks>
public class MimeMagicTests
{
    /// <summary>Writes the compiled format, so a test can state a rule instead of hex.</summary>
    private sealed class Builder
    {
        private readonly MemoryStream _bytes = new();

        public Builder() => Write("MIME-Magic\0\n"u8);

        public Builder Section(int priority, string mimeType)
        {
            Write(System.Text.Encoding.UTF8.GetBytes($"[{priority}:{mimeType}]\n"));
            return this;
        }

        public Builder Rule(
            ReadOnlySpan<byte> value,
            int indent = 0,
            int offset = 0,
            byte[]? mask = null,
            int? word = null,
            int? range = null)
        {
            if (indent > 0)
                Write(System.Text.Encoding.UTF8.GetBytes(indent.ToString()));
            Write(System.Text.Encoding.UTF8.GetBytes($">{offset}="));

            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length);
            Write(length);
            Write(value);

            if (mask is not null)
            {
                Write("&"u8);
                Write(mask);
            }

            if (word is not null)
                Write(System.Text.Encoding.UTF8.GetBytes($"~{word}"));
            if (range is not null)
                Write(System.Text.Encoding.UTF8.GetBytes($"+{range}"));

            Write("\n"u8);
            return this;
        }

        /// <summary>Writes the file into a temporary <c>mime</c> directory and loads it.</summary>
        public MimeMagic Load(TemporaryDirectory directory)
        {
            var mime = Path.Combine(directory.Path, "mime");
            System.IO.Directory.CreateDirectory(mime);
            File.WriteAllBytes(Path.Combine(mime, "magic"), _bytes.ToArray());
            return MimeMagic.Load([mime]);
        }

        private void Write(ReadOnlySpan<byte> bytes) => _bytes.Write(bytes);
    }

    [Fact]
    public void ASignatureAtTheStartOfTheFileIsRecognized()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder().Section(50, "application/pdf").Rule("%PDF-"u8).Load(temp);

        Assert.Equal("application/pdf", magic.Match("%PDF-1.7 and so on"u8));
        Assert.Null(magic.Match("not a pdf"u8));
    }

    [Fact]
    public void TheHighestPriorityMatchWins()
    {
        // Two rules can both match the same bytes — every DocBook file is also XML — and the
        // priority is the only thing that says which answer is the useful one.
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(40, "application/xml").Rule("<?xml"u8)
            .Section(90, "application/docbook+xml").Rule("<?xml"u8)
            .Load(temp);

        Assert.Equal("application/docbook+xml", magic.Match("<?xml version=\"1.0\"?>"u8));
    }

    [Fact]
    public void ANestedRuleIsARequirementAndNotAnAlternative()
    {
        // The case that makes the format worth parsing properly. The parent alone would make
        // every XML document a DocBook one, and a reader that treats children as alternatives
        // says exactly that.
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(90, "application/docbook+xml")
            .Rule("<?xml"u8)
            .Rule("-//OASIS//DTD DocBook XML"u8, indent: 1, range: 101)
            .Load(temp);

        Assert.Null(magic.Match("<?xml version=\"1.0\"?><note/>"u8));
        Assert.Equal(
            "application/docbook+xml",
            magic.Match("<?xml version=\"1.0\"?>\n<!DOCTYPE book PUBLIC \"-//OASIS//DTD DocBook XML V4.5//EN\">"u8));
    }

    [Fact]
    public void AParentWithSeveralChildrenMatchesOnAnyOneOfThem()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(90, "application/docbook+xml")
            .Rule("<?xml"u8)
            .Rule("-//OASIS//DTD DocBook"u8, indent: 1, range: 60)
            .Rule("-//KDE//DTD DocBook"u8, indent: 1, range: 60)
            .Load(temp);

        Assert.Equal("application/docbook+xml", magic.Match("<?xml ... -//KDE//DTD DocBook ..."u8));
    }

    [Fact]
    public void AValueContainingANewlineIsReadByItsLength()
    {
        // The trap the whole reader is shaped around: values are binary and a newline inside
        // one is ordinary data. A reader that scans for the terminator instead of trusting the
        // declared length mis-parses everything after it, silently.
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(50, "text/x-twoliner").Rule("first\nsecond"u8)
            .Section(50, "application/pdf").Rule("%PDF-"u8)
            .Load(temp);

        Assert.Equal("text/x-twoliner", magic.Match("first\nsecond and more"u8));
        // The section after it survived the parse, which is the half that fails quietly.
        Assert.Equal("application/pdf", magic.Match("%PDF-1.7"u8));
    }

    [Fact]
    public void AnOffsetLooksWhereItIsToldTo()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder().Section(50, "application/x-tar").Rule("ustar"u8, offset: 257).Load(temp);

        var data = new byte[300];
        "ustar"u8.CopyTo(data.AsSpan(257));
        Assert.Equal("application/x-tar", magic.Match(data));

        // The same bytes at the wrong place are not a match.
        var wrong = new byte[300];
        "ustar"u8.CopyTo(wrong.AsSpan(100));
        Assert.Null(magic.Match(wrong));
    }

    [Fact]
    public void ARangeLetsTheValueStartAnywhereInAWindow()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder().Section(50, "text/x-thing").Rule("MARK"u8, offset: 0, range: 8).Load(temp);

        Assert.Equal("text/x-thing", magic.Match("MARK"u8));
        Assert.Equal("text/x-thing", magic.Match("12345678MARK"u8));
        // One past the window.
        Assert.Null(magic.Match("123456789MARK"u8));
    }

    [Fact]
    public void AMaskComparesOnlyTheBitsItSelects()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(50, "application/x-masked")
            .Rule([0xF0], mask: [0xF0])
            .Load(temp);

        Assert.Equal("application/x-masked", magic.Match([0xF5]));
        Assert.Equal("application/x-masked", magic.Match([0xFF]));
        Assert.Null(magic.Match([0x0F]));
    }

    [Fact]
    public void AMultiByteWordIsStoredBigEndianAndComparedInHostOrder()
    {
        // Word size exists for exactly one reason: the file stores the value big-endian and
        // the bytes on disk are in the host's order. Getting this wrong matches nothing on
        // half the machines in the world and everything on the other half.
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(50, "application/x-wordy")
            .Rule([0x12, 0x34], word: 2)
            .Load(temp);

        var expected = BitConverter.IsLittleEndian ? new byte[] { 0x34, 0x12 } : [0x12, 0x34];
        Assert.Equal("application/x-wordy", magic.Match(expected));
    }

    [Fact]
    public void ARuleReachingPastTheEndOfWhatWasReadSimplyDoesNotMatch()
    {
        // A short file, or a short read. It must answer "no", not throw.
        using var temp = new TemporaryDirectory();
        var magic = new Builder().Section(50, "application/x-tar").Rule("ustar"u8, offset: 257).Load(temp);

        Assert.Null(magic.Match("tiny"u8));
        Assert.Null(magic.Match([]));
    }

    [Fact]
    public void TheFurthestReachIsReportedSoACallerKnowsHowMuchToRead()
    {
        using var temp = new TemporaryDirectory();
        var magic = new Builder()
            .Section(50, "application/x-tar").Rule("ustar"u8, offset: 257)
            .Section(50, "application/pdf").Rule("%PDF-"u8)
            .Load(temp);

        // 257 + 5, and the one-byte range that every rule has by default adds nothing.
        Assert.Equal(262, magic.MaxExtent);
    }

    [Fact]
    public void AFileThatIsNotAMagicFileIsIgnoredRatherThanMisread()
    {
        using var temp = new TemporaryDirectory();
        var mime = Path.Combine(temp.Path, "mime");
        Directory.CreateDirectory(mime);
        File.WriteAllText(Path.Combine(mime, "magic"), "this is not the compiled format");

        var magic = MimeMagic.Load([mime]);
        Assert.True(magic.IsEmpty);
        Assert.Null(magic.Match("%PDF-"u8));
    }

    [Fact]
    public void AMissingMagicFileIsNormal()
    {
        using var temp = new TemporaryDirectory();
        Assert.True(MimeMagic.Load([Path.Combine(temp.Path, "nothing-here")]).IsEmpty);
    }
}
