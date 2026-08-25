using Wlrix.Archiver.Models;

namespace Wlrix.Archiver.Services.Archives;

/// <summary>An archive open for reading, plus what can be done to it.</summary>
/// <param name="Path">The file on disk.</param>
/// <param name="Format">What it was recognized as.</param>
/// <param name="Capabilities">What the backend that opened it can do with it.</param>
/// <param name="Entries">Every member, flat. <see cref="ArchiveTree"/> turns it into the tree.</param>
/// <param name="Comment">The archive comment, where the format has one.</param>
public sealed record OpenArchive(
    string Path,
    ArchiveFormat Format,
    ArchiveCapabilities Capabilities,
    IReadOnlyList<ArchiveEntry> Entries,
    string? Comment = null);

/// <summary>One way of working with archives.</summary>
/// <remarks>
/// Ark's plugin split, for the same reason it has one: no single library covers every format in
/// every direction. SharpCompress reads nearly everything and writes zip and tar; 7z round-trips
/// only through the <c>7z</c> command; rar cannot be written at all without the proprietary
/// encoder. Rather than one class full of format special cases, each backend answers for what it
/// can handle and <see cref="ArchiveBackendRegistry"/> picks between them.
/// </remarks>
public interface IArchiveBackend
{
    /// <summary>A short name for logs and error messages.</summary>
    string Name { get; }

    /// <summary>
    /// What this backend could do with <paramref name="format"/>, or
    /// <see cref="ArchiveCapabilities.None"/> if it does not handle it at all.
    /// </summary>
    /// <remarks>
    /// Asked before the file is opened, so it can depend on the format and on whether an
    /// external tool is installed, but not on the archive's contents.
    /// </remarks>
    ArchiveCapabilities Supports(ArchiveFormat format);

    /// <summary>Reads the entry list.</summary>
    /// <param name="progress">
    /// Told how far along the read is. Reports arrive on whatever thread the backend is running
    /// on, so a UI caller should pass a <see cref="Progress{T}"/> constructed on the UI thread.
    /// </param>
    Task<OpenArchive> OpenAsync(string path, ArchiveFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="entryPaths"/> out under <paramref name="destinationDirectory"/>.
    /// </summary>
    /// <param name="entryPaths">
    /// In-archive paths to extract. Empty means everything. A directory brings its contents.
    /// </param>
    /// <param name="flatten">
    /// When true, write every file straight into the destination instead of recreating the
    /// directories above it — Ark's "extract without paths".
    /// </param>
    Task ExtractAsync(string path, ArchiveFormat format, IReadOnlyList<string> entryPaths,
        string destinationDirectory, bool flatten = false,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds files and directories from disk to the archive, under <paramref name="destinationPrefix"/>.
    /// </summary>
    Task AddAsync(string path, ArchiveFormat format, IReadOnlyList<string> sourcePaths,
        string destinationPrefix = "", CancellationToken cancellationToken = default);

    /// <summary>Deletes <paramref name="entryPaths"/>, and everything under any directory named.</summary>
    Task RemoveAsync(string path, ArchiveFormat format, IReadOnlyList<string> entryPaths,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a new, empty archive at <paramref name="path"/>.</summary>
    Task CreateAsync(string path, ArchiveFormat format,
        CancellationToken cancellationToken = default);
}

/// <summary>An archive operation failed in a way worth showing the user.</summary>
/// <remarks>
/// The point of a dedicated type is the catch clause at the call site. Everything below this
/// layer throws <see cref="IOException"/>, <see cref="InvalidOperationException"/>, or whatever
/// SharpCompress felt like; catching those individually at every command would either miss one
/// and crash the app, or turn into a bare <c>catch</c> that also swallows real bugs.
/// </remarks>
public sealed class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message)
    {
    }

    public ArchiveException(string message, Exception inner) : base(message, inner)
    {
    }
}
