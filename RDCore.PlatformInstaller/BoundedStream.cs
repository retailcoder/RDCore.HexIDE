namespace RDCore.PlatformInstaller;

/// <summary>
/// A stream read through to another, refused with an exception once more than a limit of it is read: what a decompressing
/// stream gives has no size known before it is read.
/// </summary>
/// <param name="inner">The stream read, disposed with this one.</param>
/// <param name="limit">The most bytes read from it.</param>
/// <param name="tooLarge">Makes the exception thrown once more than <paramref name="limit"/> bytes are read.</param>
internal sealed class BoundedStream(Stream inner, long limit, Func<Exception> tooLarge) : Stream
{
    private long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int bytes)
    {
        read += bytes;
        if (read > limit)
        {
            throw tooLarge();
        }

        return bytes;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
