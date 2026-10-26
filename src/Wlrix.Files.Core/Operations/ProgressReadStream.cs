namespace Wlrix.Files.Core.Operations;

/// <summary>A read stream that says how far into it the reader has got.</summary>
/// <remarks>
/// What lets one large file show progress. An operation reports once per item, and a 3 GiB
/// image copied off a share is one item for minutes on end, during which the bar sat still and
/// nothing said the copy was alive. Wrapping the source and counting what is read from it works
/// for every consumer of the bytes alike -- a plain copy, and the tar and zip writers, which do
/// their own reading.
///
/// <para>
/// Everything but reading passes straight through, seeking and length included, so a reader
/// that checks <see cref="Stream.CanSeek"/> on what it is handed sees the same answer it would
/// have for the stream underneath. The tar writer makes that check.
/// </para>
///
/// <para>
/// It is also where a pause takes hold: each read waits on the gate first, so a paused copy
/// stops within one buffer rather than at the end of the file.
/// </para>
/// </remarks>
internal sealed class ProgressReadStream(
    Stream inner, Action<long>? report, PauseGate? pause, CancellationToken cancellationToken) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        pause?.Wait(cancellationToken);
        return Counted(inner.Read(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        pause?.Wait(cancellationToken);
        return Counted(inner.Read(buffer));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (pause is not null)
            await pause.WaitAsync(token.CanBeCanceled ? token : cancellationToken).ConfigureAwait(false);
        return Counted(await inner.ReadAsync(buffer, token).ConfigureAwait(false));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Counted(int read)
    {
        if (read > 0)
        {
            _read += read;
            report?.Invoke(_read);
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
