namespace Wlrix.Files.Core.Mime;

/// <summary>
/// Naming one file, reading it only if the name does not already say.
/// </summary>
/// <remarks>
/// The read is the whole cost of sniffing, so the first thing here is the check that avoids
/// it: a name the glob tables recognize needs no bytes at all, which is the overwhelming
/// majority of files. What is left is the extensionless minority — <c>README</c>,
/// <c>configure</c>, a script somebody wrote without a suffix — and those get one bounded read
/// of the head of the file.
///
/// <para>
/// One file at a time, deliberately. Nothing here may be called for a listing: on a large
/// directory it is a read per entry, and on a share a round trip per entry, which is precisely
/// what the enumeration design exists to prevent.
/// </para>
/// </remarks>
public static class ContentSniffer
{
    /// <summary>
    /// The type of <paramref name="entry"/>, reading its head only when the name leaves it open.
    /// </summary>
    /// <remarks>
    /// Never throws for an unreadable file. A permission error while sniffing means the name's
    /// answer stands — the caller asked what kind of file this is, not whether it could be
    /// read, and failing the whole question over a failed guess would be worse than answering
    /// it less precisely.
    /// </remarks>
    public static async Task<string> ResolveAsync(
        SharedMimeDatabase mime,
        IFileSystemProvider provider,
        FileEntry entry,
        CancellationToken cancellationToken)
    {
        var byName = mime.Resolve(entry);
        if (entry.Kind is not (FileKind.File or FileKind.Unknown)
            || !mime.CanSniff
            || byName != SharedMimeDatabase.Default)
        {
            return byName;
        }

        if (entry.Size == 0)
            return SharedMimeDatabase.Empty;

        try
        {
            var head = await ReadHeadAsync(provider, entry.Location, mime.SniffLength, cancellationToken)
                .ConfigureAwait(false);
            return mime.Sniff(head) ?? byName;
        }
        catch (Exception ex) when (ex is FileOperationException or IOException)
        {
            return byName;
        }
    }

    /// <summary>Reads up to <paramref name="length"/> bytes from the start of a file.</summary>
    private static async Task<byte[]> ReadHeadAsync(
        IFileSystemProvider provider, Location location, int length, CancellationToken cancellationToken)
    {
        var fs = await provider.GetAsync(location, cancellationToken).ConfigureAwait(false);
        await using var stream = await fs.OpenReadAsync(location, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            // A short read is not the end of the stream, and treating it as one would cut a
            // signature in half on any backend that answers in chunks — which every remote
            // one does.
            var got = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (got == 0)
                break;
            read += got;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }
}
