using System.Formats.Tar;
using System.IO.Compression;

namespace Wlrix.Files.Core.Operations;

/// <summary>Names for a new archive.</summary>
public static class ArchiveNaming
{
    /// <summary>The extension a format is written with, dot included.</summary>
    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ".zip",
        _ => ".tar.gz"
    };

    /// <summary>What an archive of <paramref name="sources"/> is called before any collision.</summary>
    /// <remarks>
    /// Dolphin's choice. One item names the archive after itself, without a file's extension,
    /// so <c>report.pdf</c> becomes <c>report.zip</c> rather than <c>report.pdf.zip</c>. Several
    /// name it after the directory they came from, which is the only thing they have in common.
    /// </remarks>
    public static string Stem(IReadOnlyList<Location> sources, bool singleIsDirectory, Location home, string fallback)
    {
        if (sources.Count == 1)
        {
            var name = sources[0].Name;
            var dot = name.LastIndexOf('.');
            // A leading dot is part of the name, as it is for ConflictNaming.
            return !singleIsDirectory && dot > 0 ? name[..dot] : name;
        }
        return home.IsRoot || string.IsNullOrEmpty(home.Name) ? fallback : home.Name;
    }

    /// <summary>
    /// <c>stem.tar.gz</c>, then <c>stem (2).tar.gz</c> and up, for the caller to take the first
    /// one that is free.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ConflictNaming.NextName"/>, which puts its suffix before the last dot and
    /// would make <c>stem.tar (copy).gz</c> -- an archive that no longer looks like a tarball
    /// to anything keyed off the name. And not "(copy)" either: this is not a copy of anything.
    /// A sequence rather than a lookup, because whether a name is taken is a question for the
    /// filesystem, and on a share that is a round trip.
    /// </remarks>
    public static IEnumerable<string> Candidates(string stem, ArchiveFormat format)
    {
        var extension = Extension(format);
        yield return stem + extension;
        for (var number = 2; number < 1000; number++)
            yield return $"{stem} ({number}){extension}";
    }
}

/// <summary>Writes planned items into one archive stream.</summary>
/// <remarks>
/// Both formats come from the runtime, so the file manager carries no archive library of its
/// own; the archiver keeps SharpCompress for the formats this does not need to write.
/// </remarks>
internal abstract class ArchiveWriter : IAsyncDisposable
{
    private const int DefaultFileMode = 0b110_100_100;
    private const int DefaultDirectoryMode = 0b111_101_101;
    private const int LinkMode = 0b111_111_111;

    public static ArchiveWriter Create(ArchiveFormat format, Stream output) => format switch
    {
        ArchiveFormat.Zip => new ZipWriter(output),
        _ => new TarGzWriter(output)
    };

    /// <summary>Adds one item under <paramref name="name"/>, a relative path with forward slashes.</summary>
    public abstract Task WriteAsync(IFileSystem fs, PlannedItem item, string name, CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    protected static int ModeOf(PlannedItem item) =>
        item.IsLink ? LinkMode
        : (item.UnixMode ?? (item.IsDirectory ? DefaultDirectoryMode : DefaultFileMode)) & 0xFFF;

    /// <summary>Whether an item goes in as a link rather than as what it points at.</summary>
    /// <remarks>
    /// Only when the link's text is known. A share cannot say what a link holds, and there
    /// the file it points at is archived instead, as a copy would.
    /// </remarks>
    protected static bool AsLink(PlannedItem item, out string linkTarget)
    {
        linkTarget = item.LinkTarget ?? string.Empty;
        return item.IsLink && item.LinkTarget is not null;
    }

    private sealed class TarGzWriter : ArchiveWriter
    {
        private readonly GZipStream _gzip;
        private readonly TarWriter _tar;

        public TarGzWriter(Stream output)
        {
            _gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            // Pax, because it is the one with no limit on name length or file size.
            _tar = new TarWriter(_gzip, TarEntryFormat.Pax, leaveOpen: true);
        }

        public override async Task WriteAsync(IFileSystem fs, PlannedItem item, string name, CancellationToken cancellationToken)
        {
            if (item.IsDirectory)
            {
                await _tar.WriteEntryAsync(Entry(TarEntryType.Directory, name + "/", item), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (AsLink(item, out var linkTarget))
            {
                var link = Entry(TarEntryType.SymbolicLink, name, item);
                link.LinkName = linkTarget;
                await _tar.WriteEntryAsync(link, cancellationToken).ConfigureAwait(false);
                return;
            }

            await using var input = await fs.OpenReadAsync(item.Source, cancellationToken).ConfigureAwait(false);
            var file = Entry(TarEntryType.RegularFile, name, item);
            // A tar header carries the size ahead of the data, so with a compressed output
            // that cannot seek back to fix it, the data has to say its length up front. A local
            // file can; a remote stream is told what the scan saw.
            file.DataStream = input.CanSeek ? input : new DeclaredLengthStream(input, item.Size, item.Source);
            await _tar.WriteEntryAsync(file, cancellationToken).ConfigureAwait(false);
        }

        private static PaxTarEntry Entry(TarEntryType type, string name, PlannedItem item)
        {
            var entry = new PaxTarEntry(type, name) { Mode = (UnixFileMode)ModeOf(item) };
            if (item.Modified is { } modified)
                entry.ModificationTime = modified;
            return entry;
        }

        public override async ValueTask DisposeAsync()
        {
            await _tar.DisposeAsync().ConfigureAwait(false);
            await _gzip.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ZipWriter(Stream output) : ArchiveWriter
    {
        // The DOS date fields a zip stores cannot hold anything outside these.
        private static readonly DateTimeOffset Earliest = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Latest = new(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);

        private const int TypeDirectory = 0x4000;
        private const int TypeFile = 0x8000;
        private const int TypeLink = 0xA000;

        private readonly ZipArchive _zip = new(output, ZipArchiveMode.Create, leaveOpen: true);

        public override async Task WriteAsync(IFileSystem fs, PlannedItem item, string name, CancellationToken cancellationToken)
        {
            if (item.IsDirectory)
            {
                Entry(name + "/", item, TypeDirectory, CompressionLevel.NoCompression);
                return;
            }

            if (AsLink(item, out var linkTarget))
            {
                // Info-ZIP's convention: the link's text as the member's contents, and the
                // link type in the Unix half of the external attributes.
                var link = Entry(name, item, TypeLink, CompressionLevel.NoCompression);
                await using var text = await link.OpenAsync(cancellationToken).ConfigureAwait(false);
                await text.WriteAsync(System.Text.Encoding.UTF8.GetBytes(linkTarget), cancellationToken).ConfigureAwait(false);
                return;
            }

            var file = Entry(name, item, TypeFile, CompressionLevel.Optimal);
            await using var input = await fs.OpenReadAsync(item.Source, cancellationToken).ConfigureAwait(false);
            await using var member = await file.OpenAsync(cancellationToken).ConfigureAwait(false);
            await input.CopyToAsync(member, cancellationToken).ConfigureAwait(false);
        }

        private ZipArchiveEntry Entry(string name, PlannedItem item, int type, CompressionLevel level)
        {
            var entry = _zip.CreateEntry(name, level);
            // The permissions in the high half, which is where unzip looks for them; without
            // this every script comes back out of the archive no longer executable.
            entry.ExternalAttributes = (type | ModeOf(item)) << 16;
            var modified = item.Modified ?? DateTimeOffset.Now;
            entry.LastWriteTime = modified < Earliest ? Earliest : modified > Latest ? Latest : modified;
            return entry;
        }

        public override ValueTask DisposeAsync()
        {
            _zip.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A read-only stream that claims the length it was told, and holds itself to it.</summary>
    /// <remarks>
    /// Seekable in name only: it answers Length and Position, and a seek to where it already is,
    /// which is all the tar writer asks of it. A file that turns out shorter than it was fails the
    /// archive rather than being padded out, because padding would put zeros in the archive
    /// that nothing would ever flag.
    /// </remarks>
    private sealed class DeclaredLengthStream(Stream inner, long length, Location source) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]);
            return Advance(read, buffer.Length);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)], cancellationToken)
                .ConfigureAwait(false);
            return Advance(read, buffer.Length);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Advance(int read, int asked)
        {
            if (read == 0 && asked > 0 && _position < length)
                throw new FileOperationException(FileErrorKind.Unknown, source, "the file got shorter while it was being archived");
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var to = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset
            };
            if (to != _position)
                throw new NotSupportedException("this stream only reads forward");
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
