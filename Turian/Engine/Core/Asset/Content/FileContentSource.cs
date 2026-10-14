using Microsoft.Win32.SafeHandles;

namespace Turian.Engine.Core;

/// <summary>
/// An <see cref="IContentSource"/> over a window of an OS file handle: a whole file on desktop, or an uncompressed
/// asset inside an APK through the descriptor Android's <c>AssetManager.OpenFd</c> returns.
/// </summary>
public sealed class FileContentSource : IContentSource
{
    readonly SafeFileHandle handle;
    readonly long baseOffset;
    readonly bool ownsHandle;
    volatile bool disposed;

    /// <summary>Wraps a window of an open handle; reads never move the handle's own file position.</summary>
    /// <param name="handle">A readable handle that supports positional reads.</param>
    /// <param name="offset">Where the content starts in the handle.</param>
    /// <param name="length">The content length in bytes.</param>
    /// <param name="identity">The identity of the content.</param>
    /// <param name="ownsHandle">Whether disposing the source closes the handle.</param>
    public FileContentSource(SafeFileHandle handle, long offset, long length, ContentIdentity identity,
        bool ownsHandle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        this.handle = handle;
        baseOffset = offset;
        Length = length;
        Identity = identity;
        this.ownsHandle = ownsHandle;
    }

    /// <inheritdoc />
    public ContentIdentity Identity { get; }

    /// <inheritdoc />
    public long Length { get; }

    /// <summary>Opens a whole file; its identity is the full path plus its length and last write time.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The opened source, which owns the file handle.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static FileContentSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Content file was not found.", fullPath);
        }

        // FileShare.Delete lets a newer build replace the file by rename while a session still reads the old one.
        var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            FileOptions.RandomAccess);
        try
        {
            var length = RandomAccess.GetLength(handle);
            var identity = new ContentIdentity(fullPath, FileVersion(length, File.GetLastWriteTimeUtc(handle)));
            return new FileContentSource(handle, 0, length, identity, ownsHandle: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>The version token of a file: its length and last write time, cheap to probe and to compare.</summary>
    /// <param name="length">The file length in bytes.</param>
    /// <param name="lastWriteUtc">The file's last write time in UTC.</param>
    /// <returns>The opaque version string.</returns>
    internal static string FileVersion(long length, DateTime lastWriteUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{length}:{lastWriteUtc.Ticks}");

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length)
        {
            return 0;
        }

        var wanted = (int)Math.Min(buffer.Length, Length - offset);
        var total = 0;
        while (total < wanted)
        {
            var read = RandomAccess.Read(handle, buffer[total..wanted], baseOffset + offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <inheritdoc />
    public Stream OpenRead(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new ContentRangeStream(this, offset, length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsHandle)
        {
            handle.Dispose();
        }
    }
}
