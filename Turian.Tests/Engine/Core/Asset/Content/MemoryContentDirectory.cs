namespace Turian.Tests;

/// <summary>An in-memory content location standing in for non-file storage such as APK assets.</summary>
sealed class MemoryContentDirectory : IContentDirectory
{
    readonly Dictionary<string, byte[]> contents = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> versions = new(StringComparer.Ordinal);

    /// <summary>Gets every source opened so far, to check ownership and disposal.</summary>
    public List<MemoryContentSource> Opened { get; } = [];

    /// <inheritdoc />
    public string Id { get; } = $"memory-{Guid.NewGuid():N}";

    /// <summary>Adds or replaces content, bumping its version.</summary>
    public void Add(string path, byte[] bytes)
    {
        contents[path] = bytes;
        versions[path] = versions.GetValueOrDefault(path) + 1;
    }

    /// <summary>Adds or replaces UTF-8 text content.</summary>
    public void Add(string path, string text) => Add(path, Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public ContentIdentity? Probe(string path) =>
        contents.ContainsKey(path) ? Identity(path) : null;

    /// <inheritdoc />
    public IContentSource Open(string path)
    {
        if (!contents.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException("No content.", path);
        }

        var source = new MemoryContentSource(bytes, Identity(path));
        Opened.Add(source);
        return source;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string folder, string extension)
    {
        var prefix = string.IsNullOrEmpty(folder) ? "" : folder.TrimEnd('/') + "/";
        return
        [
            .. contents.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                    && !path[prefix.Length..].Contains('/', StringComparison.Ordinal)
                    && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    ContentIdentity Identity(string path) =>
        new(Id + ":" + path, versions[path].ToString(CultureInfo.InvariantCulture));
}

/// <summary>An <see cref="IContentSource"/> over a byte array.</summary>
sealed class MemoryContentSource(byte[] bytes, ContentIdentity identity) : IContentSource
{
    /// <summary>Gets whether the source was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public ContentIdentity Identity { get; } = identity;

    /// <inheritdoc />
    public long Length => bytes.LongLength;

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (offset >= bytes.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(buffer.Length, bytes.Length - offset);
        bytes.AsSpan((int)offset, count).CopyTo(buffer);
        return count;
    }

    /// <inheritdoc />
    public Stream OpenRead(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return new MemoryStream(bytes, (int)offset, (int)length, writable: false);
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
