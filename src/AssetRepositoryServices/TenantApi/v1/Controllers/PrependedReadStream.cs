namespace Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;

/// <summary>
/// Read-only stream that yields a fixed prefix first and then continues reading from
/// an inner stream. Used after we have peeked the first bytes of a non-seekable stream
/// for content-type sniffing and need to make those bytes available again to the caller.
/// </summary>
internal sealed class PrependedReadStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private int _prefixPosition;

    public PrependedReadStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remainingPrefix = _prefix.Length - _prefixPosition;
        if (remainingPrefix > 0)
        {
            var copy = Math.Min(count, remainingPrefix);
            Array.Copy(_prefix, _prefixPosition, buffer, offset, copy);
            _prefixPosition += copy;
            return copy;
        }
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remainingPrefix = _prefix.Length - _prefixPosition;
        if (remainingPrefix > 0)
        {
            var copy = Math.Min(buffer.Length, remainingPrefix);
            _prefix.AsSpan(_prefixPosition, copy).CopyTo(buffer.Span);
            _prefixPosition += copy;
            return copy;
        }
        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
