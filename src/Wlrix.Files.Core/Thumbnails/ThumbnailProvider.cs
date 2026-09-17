using Wlrix.Files.Core.Platform;

namespace Wlrix.Files.Core.Thumbnails;

/// <summary>
/// A thumbnail for a file: from the shared cache if there is one, rendered and cached if not.
/// </summary>
/// <remarks>
/// The order matters and is the whole of the class. Ask the cache first, because the answer is
/// usually there and reading a small PNG is far cheaper than decoding a photograph. Check the
/// failure marker second, because a file that could not be thumbnailed last time still cannot
/// and re-attempting it on every scroll is what makes a directory of broken images unusable.
/// Only then read the file.
/// </remarks>
public sealed class ThumbnailProvider(
    ThumbnailCache cache,
    CompositeThumbnailer? thumbnailers = null,
    MountTable? mounts = null,
    long maxBytes = ThumbnailProvider.DefaultMaxBytes)
{
    /// <summary>The largest file worth opening to make a picture of.</summary>
    /// <remarks>
    /// Sixty-four mebibytes. Above that the decode costs more than the thumbnail is worth, and
    /// a directory of raw camera files would otherwise read gigabytes to fill one screen.
    /// </remarks>
    public const long DefaultMaxBytes = 64 * 1024 * 1024;

    private readonly CompositeThumbnailer _thumbnailers = thumbnailers ?? CompositeThumbnailer.Default;

    // Two at a time on a four-core machine. Thumbnailing is decode-bound and competes with the
    // icon renders and the UI thread; saturating every core to fill one screenful makes
    // scrolling worse, not better.
    private readonly SemaphoreSlim _slots = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>Whether a thumbnail is even possible for this entry.</summary>
    /// <remarks>
    /// Asked before anything is queued, so the overwhelming majority of entries — every
    /// directory, every text file, everything on a share — cost one dictionary lookup rather
    /// than a queue slot.
    /// </remarks>
    public bool CanThumbnail(FileEntry entry, string mimeType) =>
        entry.Kind == FileKind.File
        && entry.Size > 0
        && (entry.Size <= maxBytes || !_thumbnailers.ReadsWholeFile(mimeType))
        && _thumbnailers.CanHandle(mimeType)
        && ThumbnailCache.CanThumbnail(entry.Location, mounts);

    /// <summary>
    /// The thumbnail for an entry as encoded PNG bytes, or null if there is none to be had.
    /// </summary>
    public async Task<byte[]?> GetAsync(
        FileEntry entry, string mimeType, ThumbnailSize size, CancellationToken cancellationToken)
    {
        if (!CanThumbnail(entry, mimeType) || !entry.Location.TryGetLocalPath(out var path))
            return null;

        var modified = entry.Modified ?? DateTimeOffset.UnixEpoch;
        if (cache.TryLoad(entry.Location, size, modified) is { } cached)
            return cached;
        if (cache.HasFailed(entry.Location, modified))
            return null;

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Asked again under the slot: fifty rows can queue for the same file while the
            // first is rendering, and forty-nine of them should find it done.
            if (cache.TryLoad(entry.Location, size, modified) is { } arrived)
                return arrived;

            var png = await _thumbnailers.RenderAsync(path, mimeType, (int)size, cancellationToken)
                .ConfigureAwait(false);
            if (png is null)
            {
                cache.StoreFailure(entry.Location, FailureMarker, modified);
                return null;
            }

            cache.Store(entry.Location, size, png, modified, entry.Size, mimeType);
            // Read back rather than returned directly, so what the caller displays is exactly
            // what a later run will load -- if the text chunks broke the file, it breaks now
            // rather than on the next launch.
            return cache.TryLoad(entry.Location, size, modified) ?? png;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // A file that cannot be read or decoded is marked so, which is what stops it being
            // re-attempted on every scroll past it.
            cache.StoreFailure(entry.Location, FailureMarker, modified);
            return null;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The smallest legal PNG: one transparent pixel.
    /// </summary>
    /// <remarks>
    /// The spec wants a real image in <c>fail/</c> so that anything browsing the directory can
    /// open it. Nothing ever displays this one — its presence is the entire message — so it is
    /// a constant rather than something encoded at runtime.
    /// </remarks>
    internal static byte[] FailureMarker { get; } =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];
}
