namespace Turian.Tests;

/// <summary>Tests the file implementation of the content source contract: ranges, concurrency, disposal, identity.</summary>
public sealed class FileContentSourceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-content-{Guid.NewGuid():N}");

    /// <summary>Creates an isolated content folder.</summary>
    public FileContentSourceTests() => Directory.CreateDirectory(Path.Combine(root, "overlays"));

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Positional reads return the requested range and stop at the end of the content.</summary>
    [Fact]
    public void ReadAtReturnsRangeAndStopsAtEnd()
    {
        using var source = FileContentSource.Open(Write("data.bin", 100));
        var buffer = new byte[10];

        Assert.Equal(100, source.Length);
        Assert.Equal(10, source.ReadAt(40, buffer));
        Assert.Equal(Pattern(100)[40..50], buffer);
        Assert.Equal(5, source.ReadAt(95, buffer));
        Assert.Equal(0, source.ReadAt(100, buffer));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadAt(-1, buffer));
    }

    /// <summary>A window over a handle offsets every read, as an APK asset descriptor does.</summary>
    [Fact]
    public void HandleWindowReadsFromItsOffset()
    {
        var path = Write("data.bin", 100);
        using var handle = File.OpenHandle(path);
        using var source = new FileContentSource(handle, 20, 30, new ContentIdentity("apk:data", "1"), ownsHandle: false);
        using var stream = source.OpenRead(0, source.Length);
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        Assert.Equal(Pattern(100)[20..50], copy.ToArray());
        source.Dispose();
        Assert.False(handle.IsClosed);
    }

    /// <summary>Streams over the same source keep their own positions and read correctly from parallel threads.</summary>
    [Fact]
    public void ConcurrentStreamsAreIndependent()
    {
        const int size = 64 * 1024;
        var expected = Pattern(size);
        using var source = FileContentSource.Open(Write("data.bin", size));

        Parallel.For(0, 32, i =>
        {
            var offset = i * 1024;
            using var stream = source.OpenRead(offset, 4096);
            var buffer = new byte[4096];
            stream.ReadExactly(buffer);
            Assert.Equal(expected.AsSpan(offset, 4096).ToArray(), buffer);
            Assert.Equal(0, stream.Read(buffer));
        });
    }

    /// <summary>A range stream seeks within its range and rejects ranges past the end of the content.</summary>
    [Fact]
    public void RangeStreamSeeksAndValidatesItsRange()
    {
        using var source = FileContentSource.Open(Write("data.bin", 100));
        using var stream = source.OpenRead(10, 20);

        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(20, stream.Length);
        stream.Seek(-5, SeekOrigin.End);
        Assert.Equal(25, stream.ReadByte());
        stream.Position = 0;
        Assert.Equal(10, stream.ReadByte());
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.OpenRead(90, 20));
    }

    /// <summary>Disposing a stream leaves the source open; disposing the source fails its reads and streams.</summary>
    [Fact]
    public void DisposalFollowsOwnership()
    {
        var source = FileContentSource.Open(Write("data.bin", 100));
        var first = source.OpenRead(0, 10);
        first.Dispose();
        var second = source.OpenRead(0, 10);
        Assert.Equal(0, second.ReadByte());

        source.Dispose();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.ReadAt(0, new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => source.OpenRead(0, 1));
        Assert.Throws<ObjectDisposedException>(() => second.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => first.ReadByte());
    }

    /// <summary>Identity names the full path and changes with the content's revision.</summary>
    [Fact]
    public void IdentityTracksRevision()
    {
        var path = Write("data.bin", 100);
        var directory = new FileContentDirectory(root);
        ContentIdentity opened;
        using (var source = directory.Open("data.bin")) opened = source.Identity;

        Assert.Equal(Path.GetFullPath(path), opened.Id);
        Assert.Equal(opened, directory.Probe("data.bin"));

        File.WriteAllBytes(path, Pattern(101));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.NotEqual(opened, directory.Probe("data.bin"));
        Assert.Null(directory.Probe("missing.bin"));
        Assert.Throws<FileNotFoundException>(() => directory.Open("missing.bin"));
    }

    /// <summary>Listing returns matching relative paths in order, and paths cannot leave the root.</summary>
    [Fact]
    public void DirectoryListsOverlaysAndStaysInsideItsRoot()
    {
        Write("overlays/b.oap", 1);
        Write("overlays/A.oap", 1);
        Write("overlays/notes.txt", 1);
        var directory = new FileContentDirectory(root);

        Assert.Equal(["overlays/A.oap", "overlays/b.oap"], directory.List("overlays", ".oap"));
        Assert.Empty(directory.List("absent", ".oap"));
        Assert.Equal(root, directory.Id);
        Assert.Throws<ArgumentException>(() => directory.Probe("../outside.bin"));
        Assert.Throws<ArgumentException>(() => directory.Open(Path.Combine(root, "overlays", "b.oap")));
    }

    string Write(string relativePath, int size)
    {
        var path = Path.Combine(root, relativePath);
        File.WriteAllBytes(path, Pattern(size));
        return path;
    }

    static byte[] Pattern(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)i;
        return bytes;
    }
}
