namespace Wlrix.Common.Progress;

/// <summary>A pass-through stream that counts the bytes read from the one underneath.</summary>
/// <remarks>
/// Wrapped around a source whose length is known, so progress can be measured against a real
/// denominator. In the archiver that is the *compressed* file, because a decompressor's own
/// output has nothing to divide it by; in a file copy it is the source file.
///
/// <para>
/// It is also the only place a long transfer can notice cancellation, since the libraries
/// underneath do not all take a token — hence <see cref="BytesRead"/> being checked by the
/// caller's loop rather than the stream doing anything clever itself.
/// </para>
/// </remarks>
public sealed class CountingStream(Stream inner) : Stream
{
    /// <summary>How many bytes have been read so far.</summary>
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

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The caller owns the inner stream — it is in a `using` of its own at the call site, and
        // disposing it twice would close the file out from under the enclosing block.
        base.Dispose(disposing);
    }
}
