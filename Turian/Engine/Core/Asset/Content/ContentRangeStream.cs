namespace Turian.Engine.Core;

/// <summary>
/// A seekable read-only stream over a range of an <see cref="IContentSource"/>. It keeps its own position and reads
/// through <see cref="IContentSource.ReadAt"/>, so streams over the same source never share state; disposing it
/// leaves the source open.
/// </summary>
sealed class ContentRangeStream : Stream
{
    readonly IContentSource source;
    readonly long start;
    long position;
    bool disposed;

    /// <summary>Opens a stream over <paramref name="length"/> bytes starting at <paramref name="offset"/>.</summary>
    /// <param name="source">The borrowed source.</param>
    /// <param name="offset">The range start within the source.</param>
    /// <param name="length">The range length in bytes.</param>
    public ContentRangeStream(IContentSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset > source.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The range runs past the end of the content.");
        }

        this.source = source;
        start = offset;
        Length = length;
    }

    /// <inheritdoc />
    public override bool CanRead => !disposed;

    /// <inheritdoc />
    public override bool CanSeek => !disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var remaining = Length - position;
        if (remaining <= 0 || buffer.IsEmpty)
        {
            return 0;
        }

        var read = source.ReadAt(start + position, buffer[..(int)Math.Min(buffer.Length, remaining)]);
        position += read;
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Attempted to seek before the start of the content range.");
        }

        position = target;
        return position;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }
}
