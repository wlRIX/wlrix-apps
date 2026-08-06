using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Wlrix.SourcePicker.Services;

/// <summary>
/// Reads one live thumbnail file published by the portal backend.
/// </summary>
/// <remarks>
/// <para>
/// The file is a fixed-size header followed by BGRA pixels, mapped once and rewritten in place
/// by the portal. Deliberately dumber than the Desks app's Wayland feed: no background thread,
/// no protocol, no connection -- a timer, a mapping, and a sequence number.
/// </para>
/// <para>
/// That sequence number is a seqlock. The portal makes it odd before writing and even after, so
/// a reader that sees an odd value, or a different value either side of its read, is looking at
/// half of one frame and half of the next and should try again rather than draw it.
/// </para>
/// </remarks>
public sealed class PreviewFeed : IDisposable
{
    /// <summary><c>WLRX</c>, little-endian. Checked before anything else in the file is believed.</summary>
    private const uint Magic = 0x58524C57;
    private const uint SupportedVersion = 1;
    private const int HeaderBytes = 32;
    private const int BytesPerPixel = 4;

    /// <summary>
    /// A ceiling on the dimensions the header may claim.
    /// </summary>
    /// <remarks>
    /// These files sit in a directory with a predictable name. A reader that maps whatever it
    /// finds there and trusts the header can be induced to allocate absurdly or read past the
    /// end of the mapping, so the claimed size is checked against both this and the file's real
    /// length before a single pixel is copied.
    /// </remarks>
    private const int MaxDimension = 4096;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _length;
    private readonly byte[] _scratch;

    private WriteableBitmap? _bitmap;
    private uint _lastSeq;

    private PreviewFeed(MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
    {
        _file = file;
        _view = view;
        _length = length;
        _scratch = new byte[length - HeaderBytes];
    }

    /// <summary>
    /// Map a preview file, or null if it cannot be used.
    /// </summary>
    /// <remarks>
    /// Never throws: a missing or malformed preview means a tile without a picture, not a
    /// picker that fails to open. The portal may also have been unable to publish one at all.
    /// </remarks>
    public static PreviewFeed? Open(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= HeaderBytes)
            {
                return null;
            }

            var file = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new PreviewFeed(file, view, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest settled frame, or null if there is not a new one to show.
    /// </summary>
    /// <remarks>
    /// Returns the same bitmap instance each time, redrawn in place, so the caller should raise
    /// a change notification rather than expect a new object.
    /// </remarks>
    public WriteableBitmap? Read()
    {
        var before = _view.ReadUInt32(24);
        // Odd: the portal is writing this very moment.
        if ((before & 1) != 0 || before == _lastSeq)
        {
            return null;
        }

        var magic = _view.ReadUInt32(0);
        var version = _view.ReadUInt32(4);
        if (magic != Magic || version != SupportedVersion)
        {
            return null;
        }

        var width = (int)_view.ReadUInt32(8);
        var height = (int)_view.ReadUInt32(12);
        var stride = (int)_view.ReadUInt32(16);
        if (!IsSane(width, height, stride))
        {
            return null;
        }

        var wanted = stride * height;
        _view.ReadArray(HeaderBytes, _scratch, 0, wanted);

        // Read the sequence again: if it moved, this is a torn frame. Dropping it costs one
        // tick, where drawing it shows a visible tear across the tile.
        if (_view.ReadUInt32(24) != before)
        {
            return null;
        }
        _lastSeq = before;

        var bitmap = Ensure(width, height);
        using (var buffer = bitmap.Lock())
        {
            // Row by row: the bitmap's own stride is not promised to match the file's.
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(_scratch, y * stride, buffer.Address + (y * buffer.RowBytes), stride);
            }
        }
        return bitmap;
    }

    /// <summary>Whether the header's claims fit both the limits and the file.</summary>
    private bool IsSane(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
        {
            return false;
        }
        if (stride < width * BytesPerPixel)
        {
            return false;
        }
        // The decisive check: the pixels the header describes must actually be inside the file.
        return (long)stride * height <= _length - HeaderBytes;
    }

    private WriteableBitmap Ensure(int width, int height)
    {
        if (_bitmap is { } existing
            && existing.PixelSize.Width == width && existing.PixelSize.Height == height)
        {
            return existing;
        }

        _bitmap?.Dispose();
        // Opaque: the portal captures `xrgb8888`, whose fourth byte is padding rather than
        // alpha. Treating it as alpha makes every tile transparent.
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        return _bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _view.Dispose();
        _file.Dispose();
    }
}
