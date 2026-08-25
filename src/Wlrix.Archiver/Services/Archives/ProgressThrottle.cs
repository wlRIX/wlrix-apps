using System.Diagnostics;
using Wlrix.Archiver.Models;

namespace Wlrix.Archiver.Services.Archives;

/// <summary>Rate-limits progress reports to something a UI can keep up with.</summary>
/// <remarks>
/// The inner loops here run tens of thousands of times — once per 128 KB of a multi-gigabyte
/// decompression, once per entry of a 29,631-entry listing — and each report crosses to the UI
/// thread. Unthrottled that is a flood of dispatcher work competing with the redraw it is
/// supposed to be driving, and the window ends up *less* responsive than with no progress at
/// all. Ten a second is more than the eye resolves on a progress bar.
/// </remarks>
internal sealed class ProgressThrottle(IProgress<ArchiveProgress>? progress)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _since = Stopwatch.StartNew();
    private bool _reported;

    /// <summary>Forwards <paramref name="value"/> if enough time has passed since the last one.</summary>
    public void Report(ArchiveProgress value)
    {
        if (progress is null)
            return;

        // The first report goes straight through: the point of it is to replace "nothing is
        // happening" as fast as possible, and waiting out the interval to do that defeats it.
        if (_reported && _since.Elapsed < Interval)
            return;

        _reported = true;
        _since.Restart();
        progress.Report(value);
    }
}

/// <summary>A pass-through stream that counts the bytes read from the one underneath.</summary>
/// <remarks>
/// Wrapped around the *compressed* file so a decompressor's progress can be measured against a
/// length that is known in advance. Reading the decompressor's own output instead would give a
/// number with nothing to divide it by.
/// </remarks>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        BytesRead += read;
        return read;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The caller owns the inner stream — it is in a `using` of its own at the call site, and
        // disposing it twice would close the file out from under the enclosing block.
        base.Dispose(disposing);
    }
}
