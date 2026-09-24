namespace Turian.Engine.Core;

/// <summary>
/// A read-only stream that exposes a bounded segment of an underlying stream, starting
/// at the underlying stream's current position. Disposing this stream also disposes the
/// underlying stream. Used to hand out a single asset blob from within a package file
/// without copying it or loading the rest of the file.
/// </summary>
/// <param name="innerStream">The underlying stream, positioned at the start of the segment.</param>
/// <param name="length">The segment length in bytes.</param>
sealed class SubStream(Stream innerStream, long length) : Stream
{
    readonly Stream innerStream = innerStream;
    long position;
    bool disposed;

    /// <inheritdoc/>
    public override bool CanRead => !disposed && innerStream.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => !disposed && innerStream.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length { get; } = length;

    /// <inheritdoc/>
    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var remaining = Length - position;
        if (remaining <= 0)
        {
            return 0;
        }

        var bytesToRead = (int)Math.Min(count, remaining);
        var bytesRead = innerStream.Read(buffer, offset, bytesToRead);
        position += bytesRead;
        return bytesRead;
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var remaining = Length - position;
        if (remaining <= 0)
        {
            return 0;
        }

        var bytesToRead = (int)Math.Min(buffer.Length, remaining);
        var bytesRead = innerStream.Read(buffer[..bytesToRead]);
        position += bytesRead;
        return bytesRead;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        var targetPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (targetPosition < 0 || targetPosition > Length)
        {
            throw new IOException("Attempted to seek outside the bounds of the packed asset stream.");
        }

        innerStream.Seek(targetPosition - position, SeekOrigin.Current);
        position = targetPosition;
        return position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            innerStream.Dispose();
        }

        disposed = true;
        base.Dispose(disposing);
    }
}
