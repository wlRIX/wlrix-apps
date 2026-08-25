using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Common.Tar;
using SharpCompress.Common.Zip;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using SharpCompress.Writers;
using Wlrix.Archiver.Models;
using Wlrix.Archiver.Services.Encodings;
using Wlrix.Common;

namespace Wlrix.Archiver.Services.Archives;

/// <summary>The managed backend, and the one that handles most formats.</summary>
/// <remarks>
/// Reads zip, tar (plain and compressed), 7z and rar; writes zip and tar. Everything runs
/// in-process with no external tool, which is what makes the common cases work on a machine
/// with nothing installed.
/// </remarks>
public sealed class SharpCompressBackend : IArchiveBackend
{
    private readonly FilenameDecoder _decoder;

    public SharpCompressBackend(FilenameDecoder decoder) => _decoder = decoder;

    public string Name => "sharpcompress";

    public ArchiveCapabilities Supports(ArchiveFormat format) => format switch
    {
        // Random-access containers it can rewrite.
        ArchiveFormat.Zip or ArchiveFormat.Tar => ArchiveCapabilities.All,

        // A compressed tar can be read and rewritten, but "create empty" is not offered:
        // an empty tar.gz is a valid file that no tool produces on purpose, and File / New
        // has a format picker that can simply not list it.
        ArchiveFormat.TarGz or ArchiveFormat.TarBz2 =>
            ArchiveCapabilities.Read | ArchiveCapabilities.Extract
            | ArchiveCapabilities.Add | ArchiveCapabilities.Remove,

        // Readable, but SharpCompress has no xz *compressor*, so a rewrite would have to change
        // the format under the user.
        ArchiveFormat.TarXz => ArchiveCapabilities.ReadOnly,

        // Zstandard has neither a compressor nor a decompressor here: `ArchiveFactory` answers
        // "Cannot determine compressed stream type" for a .tar.zst. Claiming read-only would
        // turn that into an error at open time instead of the honest "not a format this
        // application can read" the registry produces for None.
        ArchiveFormat.TarZst => ArchiveCapabilities.None,

        // No encoder for 7z, and none for rar at all — see SevenZipCliBackend for the former.
        ArchiveFormat.SevenZip or ArchiveFormat.Rar => ArchiveCapabilities.ReadOnly,

        // One member, no directory: nothing to add to or remove from.
        ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz or ArchiveFormat.Lzip =>
            ArchiveCapabilities.ReadOnly,

        _ => ArchiveCapabilities.None,
    };

    public Task<OpenArchive> OpenAsync(string path, ArchiveFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            // A fresh archive means fresh evidence; a code page detected from the last one must
            // not carry over into this one's names.
            _decoder.Reset();

            if (format.IsSingleStream())
                return new OpenArchive(path, format, Supports(format), [SingleStreamEntry(path)]);

            using var session = OpenSession(path, format, progress, cancellationToken);
            var entries = new List<ArchiveEntry>();
            var throttle = new ProgressThrottle(progress);
            foreach (var entry in session.Archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Convert(entry, path, format) is { } converted)
                    entries.Add(converted);

                // A count, not a percentage: an archive does not say how many entries it holds
                // until they have all been read, so there is nothing honest to divide by.
                throttle.Report(new ArchiveProgress(ArchivePhase.Reading, Count: entries.Count));
            }

            return new OpenArchive(path, format, Supports(format), entries);
        }, cancellationToken);

    public Task ExtractAsync(string path, ArchiveFormat format, IReadOnlyList<string> entryPaths,
        string destinationDirectory, bool flatten = false,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(destinationDirectory);
            var wanted = new EntrySelection(entryPaths);
            var options = new ExtractionOptions
            {
                ExtractFullPath = !flatten,
                Overwrite = true,
                PreserveFileTime = true,
            };

            if (format.IsSingleStream())
            {
                ExtractSingleStream(path, format, destinationDirectory);
                return;
            }

            using var session = OpenSession(path, format, progress, cancellationToken);
            var throttle = new ProgressThrottle(progress);
            var written = 0;
            foreach (var entry in session.Archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory || IsTarMetadata(entry.Key)
                    || !wanted.Contains(Normalize(entry.Key)))
                {
                    continue;
                }

                // Guarded rather than trusted: an entry named `../../.ssh/authorized_keys` is
                // the oldest trick against an extractor, and SharpCompress does not check.
                var target = ResolveTarget(destinationDirectory, Normalize(entry.Key), flatten);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.WriteToFile(target, options);
                throttle.Report(new ArchiveProgress(ArchivePhase.Extracting, Count: ++written));
            }
        }, cancellationToken);

    public Task AddAsync(string path, ArchiveFormat format, IReadOnlyList<string> sourcePaths,
        string destinationPrefix = "", CancellationToken cancellationToken = default) =>
        Rewrite(path, format, cancellationToken, archive =>
        {
            foreach (var source in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(source))
                    AddDirectory(archive, source, destinationPrefix);
                else if (File.Exists(source))
                    AddFile(archive, source, JoinInArchive(destinationPrefix,
                        Path.GetFileName(source)));
            }
        });

    public Task RemoveAsync(string path, ArchiveFormat format, IReadOnlyList<string> entryPaths,
        CancellationToken cancellationToken = default) =>
        Rewrite(path, format, cancellationToken, archive =>
        {
            var doomed = new EntrySelection(entryPaths);
            // Materialized first: RemoveEntry mutates the collection being walked. The metadata
            // entries are skipped because they belong to the file after them, not to
            // themselves — dropping one on its own would strip a real entry of its long name.
            var victims = archive.Entries
                .Where(entry => !IsTarMetadata(entry.Key)
                                && doomed.Contains(Normalize(entry.Key)))
                .ToList();
            foreach (var entry in victims)
                archive.RemoveEntry(entry);
        });

    public Task CreateAsync(string path, ArchiveFormat format,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var archive = NewWritable(format);
            using var stream = File.Create(path);
            archive.SaveTo(stream, WriterFor(format));
        }, cancellationToken);

    /// <summary>An open archive, plus any scratch file it took to get at one.</summary>
    private sealed class Session(IArchive archive, Stream? stream, string? scratch) : IDisposable
    {
        public IArchive Archive { get; } = archive;

        /// <summary>The archive as something that can be written back, if the format allows.</summary>
        public IWritableArchive Writable => Archive as IWritableArchive
            ?? throw new ArchiveException("This archive cannot be modified.");

        public void Dispose()
        {
            Archive.Dispose();
            stream?.Dispose();
            if (scratch is null)
                return;

            try
            {
                File.Delete(scratch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Scratch files outliving the process is untidy, not broken.
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> for random access, with the filename decoder wired in.
    /// </summary>
    /// <remarks>
    /// A compressed tar takes the long way round, and it has to.
    /// <c>ArchiveFactory.Open</c> on a <c>.tar.gz</c> sees the gzip wrapper and stops there: it
    /// answers a GZip archive holding exactly one entry, named after the tar inside. Nothing
    /// fails, and the window shows a single row called <c>backup.tar</c> instead of the
    /// hundreds of files that are actually in it. So the wrapper is peeled off into a scratch
    /// file first and the tar is opened from that.
    ///
    /// A file rather than a <c>MemoryStream</c> because a tarball is exactly the kind of thing
    /// that does not fit in memory, and random access is the whole point — a forward-only
    /// reader would do for listing but not for editing.
    /// </remarks>
    private Session OpenSession(string path, ArchiveFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!format.IsTar() || format == ArchiveFormat.Tar)
                return new Session(ArchiveFactory.Open(path, ReaderOptions()), null, null);

            var scratch = Decompress(path, format, progress, cancellationToken);
            var stream = File.OpenRead(scratch);
            return new Session(TarArchive.Open(stream, ReaderOptions()), stream, scratch);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or ArchiveException or NotSupportedException)
        {
            throw new ArchiveException($"Could not open {Path.GetFileName(path)}.", ex);
        }
    }

    /// <summary>Unwraps a compressed tar into a scratch file and returns its path.</summary>
    /// <remarks>
    /// <b>Not <see cref="Path.GetTempPath"/>.</b> On this desktop — and on most systemd ones —
    /// <c>/tmp</c> is a tmpfs, so a scratch file there is RAM. A 3.1 GB <c>.tar.gz</c> unpacks to
    /// 9.3 GB of tar, and putting that in memory to read a *listing* is not a trade worth making.
    /// The app's own data directory is on real disk, and is where <see cref="DragStaging"/>
    /// already puts its scratch for the same reason.
    ///
    /// Progress is reported against the *compressed* length, because that is the number that is
    /// known up front — a gzip trailer records the uncompressed size only modulo 4 GB, which for
    /// this archive would read as 0.9 GB and hit "100%" four times over.
    /// </remarks>
    private static string Decompress(string path, ArchiveFormat format,
        IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = ApplicationPaths.EnsureDirectory(
            Path.Combine(ApplicationPaths.AppData, "archiver", "scratch"));
        var scratch = Path.Combine(directory, $"{Guid.NewGuid():N}.tar");
        try
        {
            var total = new FileInfo(path).Length;
            using var source = File.OpenRead(path);
            using var counted = new CountingStream(source);
            using var decompressed = Decompressor(counted, format);
            using var destination = File.Create(scratch);

            var throttle = new ProgressThrottle(progress);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = decompressed.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                throttle.Report(new ArchiveProgress(ArchivePhase.Decompressing,
                    total > 0 ? Math.Min(1.0, (double)counted.BytesRead / total) : null));
            }

            return scratch;
        }
        catch
        {
            File.Delete(scratch);
            throw;
        }
    }

    private static Stream Decompressor(Stream source, ArchiveFormat format) => format switch
    {
        ArchiveFormat.TarGz => new GZipStream(source, CompressionMode.Decompress),
        // `decompressConcatenated`, because a .tar.bz2 built by `pbzip2` is several bzip2
        // streams end to end and a reader that stops at the first one silently truncates it.
        ArchiveFormat.TarBz2 => new BZip2Stream(source, CompressionMode.Decompress,
            decompressConcatenated: true),
        ArchiveFormat.TarXz => new XZStream(source),
        _ => throw new ArchiveException($"{format} archives cannot be read."),
    };

    /// <summary>
    /// The one entry a bare <c>.gz</c>, <c>.bz2</c>, <c>.xz</c> or <c>.lz</c> stands for.
    /// </summary>
    /// <remarks>
    /// These formats hold a single compressed stream and no directory at all — there is no
    /// entry list to read, and `ArchiveFactory` only knows how to open the gzip one. The
    /// listing gets a single synthetic row named after the archive with its compression suffix
    /// removed, which is what the file will be called once it is written out.
    ///
    /// The size is unknown and stays null: it is recorded nowhere a reader can reach without
    /// decompressing the whole stream, and guessing would be worse than a blank column.
    /// </remarks>
    private static ArchiveEntry SingleStreamEntry(string path) => new(
        Path: Path.GetFileNameWithoutExtension(path),
        IsDirectory: false,
        Size: null,
        CompressedSize: new FileInfo(path).Length,
        Modified: File.GetLastWriteTime(path),
        Mode: null,
        Owner: null,
        Group: null);

    /// <summary>Decompresses a single-stream archive into the destination directory.</summary>
    private static void ExtractSingleStream(string path, ArchiveFormat format,
        string destinationDirectory)
    {
        var target = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(path));
        using var source = File.OpenRead(path);
        using var decompressed = SingleStreamDecompressor(source, format);
        using var destination = File.Create(target);
        decompressed.CopyTo(destination);
    }

    private static Stream SingleStreamDecompressor(Stream source, ArchiveFormat format) =>
        format switch
        {
            ArchiveFormat.GZip => new GZipStream(source, CompressionMode.Decompress),
            ArchiveFormat.BZip2 => new BZip2Stream(source, CompressionMode.Decompress,
                decompressConcatenated: true),
            ArchiveFormat.Xz => new XZStream(source),
            ArchiveFormat.Lzip => new LZipStream(source, CompressionMode.Decompress),
            _ => throw new ArchiveException($"{format} archives cannot be read."),
        };

    /// <summary>
    /// Whether a tar entry is bookkeeping rather than a file.
    /// </summary>
    /// <remarks>
    /// PAX and GNU tar store the metadata that will not fit in the 512-byte header — long
    /// names, high uids, sub-second times — as extra entries in the stream itself, and
    /// SharpCompress surfaces them alongside the real ones. Every PAX tarball would otherwise
    /// show a <c>@PaxHeader</c> row between each pair of files.
    /// </remarks>
    internal static bool IsTarMetadata(string? key)
    {
        // Every component, not just the last: the two writers put the marker in different
        // places. PAX names its records `././@PaxHeader`, where it is the leaf; GNU names them
        // `PaxHeaders.<pid>/<the real name>`, where it is the *directory* and the leaf is the
        // ordinary filename.
        foreach (var component in Normalize(key).Split('/'))
        {
            if (component is "@PaxHeader" or "@LongLink" or "@LongName" or "pax_global_header"
                || component.StartsWith("PaxHeaders", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reader options carrying the custom decoder.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the app uses SharpCompress rather than <c>System.IO.Compression</c>:
    /// the hook that lets a Shift-JIS name be read as Japanese instead of mojibake. Built fresh
    /// each time so a change from the View menu takes effect on the next read.
    /// </remarks>
    private ReaderOptions ReaderOptions() => new()
    {
        ArchiveEncoding = new ArchiveEncoding { CustomDecoder = _decoder.CustomDecoder },
    };

    /// <summary>
    /// Applies <paramref name="edit"/> and writes the archive back.
    /// </summary>
    /// <remarks>
    /// Through a temporary file, always. <c>SaveTo</c> re-serializes the whole archive from a
    /// model that is still reading the original — writing over the source would truncate the
    /// input mid-read and leave nothing behind. Renaming into place afterwards also means a
    /// crash halfway through costs the edit rather than the archive.
    /// </remarks>
    private Task Rewrite(string path, ArchiveFormat format, CancellationToken cancellationToken,
        Action<IWritableArchive> edit) =>
        Task.Run(() =>
        {
            var temporary = path + ".wlrix-new";
            try
            {
                using (var session = OpenSession(path, format))
                {
                    edit(session.Writable);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = File.Create(temporary);
                    // The writer re-applies the compression, so a tar unwrapped into a scratch
                    // file on the way in comes back out as a .tar.gz rather than a bare tar.
                    session.Writable.SaveTo(stream, WriterFor(format));
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or NotSupportedException)
            {
                throw new ArchiveException(
                    $"Could not write {Path.GetFileName(path)}.", ex);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }, cancellationToken);

    private static IWritableArchive NewWritable(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => ZipArchive.Create(),
        _ when format.IsTar() => TarArchive.Create(),
        _ => throw new ArchiveException($"{format} archives cannot be created."),
    };

    /// <summary>The compression to write back with, matching what was read.</summary>
    /// <remarks>
    /// Getting this wrong is silent and destructive: saving a <c>.tar.gz</c> with
    /// <see cref="CompressionType.None"/> leaves an uncompressed tar under a name that says
    /// otherwise, which every other tool will then fail to open.
    /// </remarks>
    private static WriterOptions WriterFor(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => new WriterOptions(CompressionType.Deflate),
        ArchiveFormat.TarGz => new WriterOptions(CompressionType.GZip),
        ArchiveFormat.TarBz2 => new WriterOptions(CompressionType.BZip2),
        _ => new WriterOptions(CompressionType.None),
    };

    private static void AddFile(IWritableArchive archive, string source, string entryPath)
    {
        var info = new FileInfo(source);
        // `closeStream: true` hands ownership to the archive, which closes it after SaveTo has
        // consumed it — the stream must still be open then, so it cannot be a `using` here.
        archive.AddEntry(entryPath, info.OpenRead(), closeStream: true, info.Length,
            info.LastWriteTime);
    }

    private static void AddDirectory(IWritableArchive archive, string source, string prefix)
    {
        var root = JoinInArchive(prefix, Path.GetFileName(source.TrimEnd('/')));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            AddFile(archive, file, JoinInArchive(root, relative));
        }
    }

    private static string JoinInArchive(string prefix, string name) =>
        prefix.Length == 0 ? name : $"{prefix.TrimEnd('/')}/{name}";

    private static string Normalize(string? key) => ArchiveTree.Normalize(key ?? string.Empty);

    /// <summary>
    /// The file to write an entry to, refusing anything that would land outside
    /// <paramref name="destinationDirectory"/>.
    /// </summary>
    private static string ResolveTarget(string destinationDirectory, string entryPath,
        bool flatten)
    {
        var relative = flatten ? Path.GetFileName(entryPath) : entryPath;
        var full = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
        var root = Path.GetFullPath(destinationDirectory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && full != root)
        {
            throw new ArchiveException(
                $"The archive contains an entry that would be written outside the destination: {entryPath}");
        }

        return full;
    }

    /// <summary>Reads the metadata the listing shows off one entry.</summary>
    /// <remarks>
    /// Mode, owner and group are per-format and reached by casting: tar records them properly,
    /// a zip written on Unix hides the mode in the top 16 bits of its external attributes, and
    /// nothing else has them at all. Hence the nulls rather than zeroes — an unknown mode and a
    /// mode of 000 are not the same thing, and the column has to be able to stay blank.
    /// </remarks>
    private static ArchiveEntry? Convert(IArchiveEntry entry, string archivePath,
        ArchiveFormat format)
    {
        if (IsTarMetadata(entry.Key))
            return null;

        var key = Normalize(entry.Key);
        if (key.Length == 0)
        {
            // A single-stream format (.gz, .xz) has one nameless member. Name it after the
            // archive with the compression suffix taken off, which is what the member will be
            // called once it is written out.
            if (!format.IsSingleStream())
                return null;
            key = Path.GetFileNameWithoutExtension(archivePath);
        }

        var (mode, owner, group) = Ownership(entry);
        return new ArchiveEntry(
            Path: key,
            IsDirectory: entry.IsDirectory,
            Size: entry.Size >= 0 ? entry.Size : null,
            CompressedSize: entry.CompressedSize > 0 ? entry.CompressedSize : null,
            Modified: entry.LastModifiedTime,
            Mode: mode,
            Owner: owner,
            Group: group,
            IsEncrypted: entry.IsEncrypted,
            LinkTarget: string.IsNullOrEmpty(entry.LinkTarget) ? null : entry.LinkTarget);
    }

    /// <summary>
    /// The permission bits of a tar entry, with SharpCompress's directory marker taken back out.
    /// </summary>
    /// <remarks>
    /// SharpCompress ORs <c>0o1000</c> into <see cref="TarEntry.Mode"/> for every directory,
    /// whatever the header actually said. That bit is the sticky bit, so a plain <c>0o755</c>
    /// directory reads back as <c>0o1755</c> and renders as <c>drwxr-xr-t</c> — which is what
    /// every directory of every tarball showed before this. Verified against a tar holding a
    /// plain, a sticky and a setgid directory: the first two come back byte-identical, so the
    /// real sticky bit is simply not recoverable through this API.
    ///
    /// Given that, dropping it is the least wrong answer. Sticky directories are rare and
    /// ordinary ones are not, so keeping the bit mislabels almost every row to avoid mislabeling
    /// a few. setgid survives (<c>0o3755</c> stays distinguishable from <c>0o1755</c>), and files
    /// are untouched by the quirk, so setuid, setgid and sticky all still show on those.
    /// </remarks>
    private static int TarMode(TarEntry tar)
    {
        const int Sticky = 0x200;
        var mode = (int)(tar.Mode & 0xfff);
        return tar.IsDirectory ? mode & ~Sticky : mode;
    }

    private static (int? Mode, string? Owner, string? Group) Ownership(IArchiveEntry entry)
    {
        switch (entry)
        {
            case TarEntry tar:
                // Tar stores all three. The names are not in the header SharpCompress exposes,
                // so the numeric ids are what the columns get.
                return (
                    TarMode(tar),
                    tar.UserID.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    tar.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Zip's external file attributes are only Unix mode bits when the archive was
            // written on a Unix host; on a DOS-made zip the same field holds FAT attributes and
            // reading it as a mode would print nonsense. The high half being zero is the
            // available tell, and it is the one Info-ZIP itself uses.
            case ZipEntry { Attrib: { } attrib } when (attrib >> 16) != 0:
                return ((attrib >> 16) & 0xfff, null, null);

            default:
                return (null, null, null);
        }
    }
}

/// <summary>
/// The set of entries a command was asked to act on, with directories covering their contents.
/// </summary>
/// <remarks>
/// Selecting a directory in the tree has to mean the directory and everything under it, and the
/// archive's flat entry list has no idea about that relationship. Empty means everything, which
/// is what the brief's "extract selected or all contents if none" comes to.
/// </remarks>
internal sealed class EntrySelection
{
    private readonly HashSet<string> _exact;
    private readonly string[] _prefixes;
    private readonly bool _everything;

    public EntrySelection(IReadOnlyList<string> entryPaths)
    {
        _everything = entryPaths.Count == 0;
        _exact = new HashSet<string>(entryPaths.Select(ArchiveTree.Normalize), StringComparer.Ordinal);
        _prefixes = _exact.Select(path => path + "/").ToArray();
    }

    public bool Contains(string entryPath) =>
        _everything
        || _exact.Contains(entryPath)
        || _prefixes.Any(prefix => entryPath.StartsWith(prefix, StringComparison.Ordinal));
}
