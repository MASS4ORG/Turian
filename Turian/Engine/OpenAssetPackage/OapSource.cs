using Microsoft.Win32.SafeHandles;

namespace OpenAssetPackage;

/// <summary>
/// Random-access bytes an <see cref="OapReader"/> reads a package from: a file, a window of an APK, memory or a
/// network cache. Positional reads must be safe from any number of threads at once.
/// </summary>
public interface IOapSource
{
    /// <summary>Gets the length of the package in bytes.</summary>
    long Length { get; }

    /// <summary>Reads bytes at an absolute offset without touching any shared position.</summary>
    /// <param name="offset">The offset from the start of the package.</param>
    /// <param name="buffer">The destination; reading stops at its length or at the end of the package.</param>
    /// <returns>The number of bytes read; <c>0</c> at or past the end.</returns>
    int ReadAt(long offset, Span<byte> buffer);
}

/// <summary>An <see cref="IOapSource"/> over a byte array, which it references without copying.</summary>
/// <param name="bytes">The package bytes.</param>
public sealed class OapMemorySource(byte[] bytes) : IOapSource
{
    readonly byte[] bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));

    /// <inheritdoc />
    public long Length => bytes.LongLength;

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= bytes.LongLength) return 0;
        var count = (int)Math.Min(buffer.Length, bytes.LongLength - offset);
        bytes.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }
}

/// <summary>An <see cref="IOapSource"/> over a file, read with positional I/O; it owns the file handle.</summary>
public sealed class OapFileSource : IOapSource, IDisposable
{
    readonly SafeFileHandle handle;

    OapFileSource(SafeFileHandle handle, long length)
    {
        this.handle = handle;
        Length = length;
    }

    /// <inheritdoc />
    public long Length { get; }

    /// <summary>Opens a package file for shared reading.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The opened source.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static OapFileSource Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("OAP package was not found.", fullPath);

        // FileShare.Delete lets a newer build replace the package by rename while this reader still holds it.
        var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            FileOptions.RandomAccess);
        try
        {
            return new OapFileSource(handle, RandomAccess.GetLength(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length) return 0;

        var wanted = (int)Math.Min(buffer.Length, Length - offset);
        var total = 0;
        while (total < wanted)
        {
            var read = RandomAccess.Read(handle, buffer[total..wanted], offset + total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    /// <summary>Closes the file handle.</summary>
    public void Dispose() => handle.Dispose();
}
