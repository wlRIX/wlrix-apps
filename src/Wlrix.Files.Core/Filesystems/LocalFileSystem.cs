using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace Wlrix.Files.Core.Filesystems;

/// <summary>The local filesystem.</summary>
/// <remarks>
/// Enumeration goes through <see cref="FileSystemEnumerable{T}"/> rather than
/// <c>DirectoryInfo.EnumerateFileSystemInfos</c>, and that is load-bearing for the
/// hundred-thousand-entry target: the low-level enumerator hands the transform a
/// <see cref="FileSystemEntry"/> that already carries the size, timestamps and
/// attributes from the <c>statx</c> the directory walk performed, so building an entry
/// costs no extra syscall. Reading those off a <c>FileInfo</c> re-stats every file.
///
/// <para>
/// Everything blocking is pushed to the thread pool. Callers are on the UI thread and
/// a directory on a sleeping disk can block for seconds.
/// </para>
/// </remarks>
public sealed class LocalFileSystem : IFileSystem
{
    /// <summary>The single mount key every local location shares.</summary>
    public const string LocalMountKey = "file://";

    /// <inheritdoc/>
    public string MountKey => LocalMountKey;

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.Rename
        | FileSystemCapabilities.HardLink
        | FileSystemCapabilities.SymLink
        | FileSystemCapabilities.PosixMode
        | FileSystemCapabilities.Trash
        | FileSystemCapabilities.Watch
        | FileSystemCapabilities.RandomWriteAccess
        | FileSystemCapabilities.PreservesMTime
        | FileSystemCapabilities.ReportsFreeSpace
        | FileSystemCapabilities.CaseSensitive;

    /// <inheritdoc/>
    public Task<FileStat> StatAsync(Location location, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() =>
        {
            try
            {
                // Symlinks are resolved for Kind but the link target is still reported,
                // so the UI can mark it as a link while treating it as what it points at.
                var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
                if (!info.Exists)
                    throw new FileOperationException(FileErrorKind.NotFound, location);

                return new FileStat
                {
                    Location = location,
                    Kind = KindOf(info),
                    Size = info is FileInfo f ? f.Length : 0,
                    Modified = info.LastWriteTimeUtc,
                    UnixMode = TryGetMode(path),
                    SymlinkTarget = info.LinkTarget
                };
            }
            catch (Exception ex) when (ex is not FileOperationException and not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(Location location, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() => File.Exists(path) || Directory.Exists(path), cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        Location location, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);

        // The enumerable is lazy, so constructing it does no IO and cannot fail; the
        // directory-missing error surfaces on the first MoveNext instead. Hence the
        // explicit existence check, which turns that into the right error before any
        // partial results have been yielded.
        if (!await Task.Run(() => Directory.Exists(path), cancellationToken).ConfigureAwait(false))
            throw new FileOperationException(
                File.Exists(path) ? FileErrorKind.NotADirectory : FileErrorKind.NotFound, location);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            // Hidden files are the caller's to filter: "show hidden" is a view
            // setting, and re-reading the directory to toggle it would be absurd.
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            // Do not follow links during the walk. A symlink loop inside a directory
            // would otherwise be an infinite listing.
            ReturnSpecialDirectories = false
        };

        // Chunked off the thread pool rather than yielded one at a time: an async
        // enumerable that hops threads per item would cost more in scheduling than
        // the enumeration itself.
        using var enumerator = new FileSystemEnumerable<FileEntry>(path, Transform, options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry _) => true
        }.GetEnumerator();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<FileEntry> chunk;
            try
            {
                chunk = await Task.Run(() =>
                {
                    var buffer = new List<FileEntry>(512);
                    while (buffer.Count < 512 && enumerator.MoveNext())
                        buffer.Add(enumerator.Current);
                    return buffer;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }

            foreach (var entry in chunk)
                yield return entry;

            if (chunk.Count < 512)
                yield break;
        }

        FileEntry Transform(ref FileSystemEntry entry)
        {
            var name = entry.FileName.ToString();
            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            return new FileEntry
            {
                Location = location.Child(name),
                Name = name,
                // entry.IsDirectory already follows the link for us, so a symlink to a
                // directory sorts and opens as a directory -- which is what a user
                // means by it. SymlinkTarget is what marks it as a link.
                Kind = entry.IsDirectory ? FileKind.Directory : isLink ? FileKind.Symlink : FileKind.File,
                Size = entry.IsDirectory ? 0 : entry.Length,
                Modified = entry.LastWriteTimeUtc,
                // Mode is deliberately not read here: it needs a syscall per entry, and
                // the details view does not show it. StatAsync has it for Properties.
                UnixMode = null,
                SymlinkTarget = null,
                IsHidden = name.Length > 0 && name[0] == '.'
            };
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(Location location, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run<Stream>(() =>
        {
            try
            {
                return new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Stream> OpenWriteAsync(Location location, WriteMode mode, long? length, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run<Stream>(() =>
        {
            try
            {
                var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = mode switch
                    {
                        WriteMode.CreateNew => FileMode.CreateNew,
                        WriteMode.Append => FileMode.Append,
                        _ => FileMode.Create
                    },
                    Access = mode == WriteMode.Append ? FileAccess.Write : FileAccess.ReadWrite,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous,
                    // Preallocating turns a doomed multi-gigabyte copy into an
                    // immediate ENOSPC instead of one discovered near the end, and
                    // keeps the file less fragmented besides.
                    PreallocationSize = mode == WriteMode.Append ? 0 : length ?? 0
                });
                return stream;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task CreateDirectoryAsync(Location location, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() =>
        {
            // Directory.CreateDirectory is silently fine with an existing directory,
            // but a caller creating a new folder wants to know the name was taken.
            if (Directory.Exists(path) || File.Exists(path))
                throw new FileOperationException(FileErrorKind.AlreadyExists, location);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Location location, bool recursive, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() =>
        {
            try
            {
                // A symlink to a directory must be unlinked, never recursed into --
                // otherwise deleting a link wipes the target's contents.
                var info = new FileInfo(path);
                if (info.Exists || info.LinkTarget is not null)
                {
                    info.Delete();
                    return;
                }
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive);
                    return;
                }
                throw new FileOperationException(FileErrorKind.NotFound, location);
            }
            catch (Exception ex) when (ex is not FileOperationException and not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RenameAsync(Location from, Location to, CancellationToken cancellationToken)
    {
        var source = RequireLocal(from);
        var target = RequireLocal(to);
        return Task.Run(() =>
        {
            try
            {
                if (File.Exists(target) || Directory.Exists(target))
                    throw new FileOperationException(FileErrorKind.AlreadyExists, to);
                if (Directory.Exists(source))
                    Directory.Move(source, target);
                else
                    File.Move(source, target, overwrite: false);
            }
            catch (Exception ex) when (ex is not FileOperationException and not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, to);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>File.CreateSymbolicLink</c> rather than the <c>Directory</c> overload even when the
    /// target is one: on Linux both reach the same <c>symlink(2)</c>, and choosing between
    /// them would mean resolving the target first — which a link to something that does not
    /// exist yet, or to a path only meaningful from the link's own directory, would fail.
    /// </remarks>
    public Task CreateSymlinkAsync(Location link, string target, CancellationToken cancellationToken)
    {
        var path = RequireLocal(link);
        return Task.Run(() =>
        {
            if (File.Exists(path) || Directory.Exists(path))
                throw new FileOperationException(FileErrorKind.AlreadyExists, link);
            try
            {
                File.CreateSymbolicLink(path, target);
            }
            catch (Exception ex) when (ex is not FileOperationException and not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, link);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SetModifiedAsync(Location location, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.SetLastWriteTimeUtc(path, modified.UtcDateTime);
                else
                    File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SetUnixModeAsync(Location location, int mode, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run(() =>
        {
            try
            {
                File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw LocalErrorClassifier.Classify(ex, location);
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<FreeSpace?> GetFreeSpaceAsync(Location location, CancellationToken cancellationToken)
    {
        var path = RequireLocal(location);
        return Task.Run<FreeSpace?>(() =>
        {
            try
            {
                var drive = new DriveInfo(path);
                return new FreeSpace(drive.TotalSize, drive.AvailableFreeSpace);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Free space is decoration on a status bar. A filesystem that will not
                // report it is not a reason to fail whatever the caller was doing.
                return null;
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string RequireLocal(Location location) =>
        location.TryGetLocalPath(out var path)
            ? path
            : throw new FileOperationException(FileErrorKind.Unsupported, location,
                $"{nameof(LocalFileSystem)} was handed a non-local location: {location}");

    private static FileKind KindOf(FileSystemInfo info)
    {
        if (info is DirectoryInfo)
            return FileKind.Directory;
        return info.LinkTarget is not null ? FileKind.Symlink : FileKind.File;
    }

    private static int? TryGetMode(string path)
    {
        try
        {
            return (int)File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
