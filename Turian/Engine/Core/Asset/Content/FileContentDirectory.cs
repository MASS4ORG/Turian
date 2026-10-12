namespace Turian.Engine.Core;

/// <summary>An <see cref="IContentDirectory"/> over a folder on disk, such as the one an exported game runs from.</summary>
public sealed class FileContentDirectory : IContentDirectory
{
    readonly string root;

    /// <summary>Uses a folder as a content location; it need not exist yet.</summary>
    /// <param name="rootPath">The folder path.</param>
    public FileContentDirectory(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    /// <inheritdoc />
    public string Id => root;

    /// <inheritdoc />
    public ContentIdentity? Probe(string path)
    {
        var info = new FileInfo(Resolve(path));
        return info.Exists
            ? new ContentIdentity(info.FullName, FileContentSource.FileVersion(info.Length, info.LastWriteTimeUtc))
            : null;
    }

    /// <inheritdoc />
    public IContentSource Open(string path) => FileContentSource.Open(Resolve(path));

    /// <inheritdoc />
    public IReadOnlyList<string> List(string folder, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        var directory = string.IsNullOrEmpty(folder) ? root : Resolve(folder);
        if (!Directory.Exists(directory)) return [];

        return
        [
            .. Directory.EnumerateFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly)
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    // Content paths are relative and never escape the root, so an overlay or a mod cannot name arbitrary files.
    string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
            throw new ArgumentException($"Content path '{path}' must be relative to the content root.", nameof(path));

        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Content path '{path}' leaves the content root.", nameof(path));
        return full;
    }
}
