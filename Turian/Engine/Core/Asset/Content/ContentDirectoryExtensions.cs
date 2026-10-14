namespace Turian.Engine.Core;

/// <summary>Whole-content reads over an <see cref="IContentDirectory"/>, for small files such as catalogs and indexes.</summary>
public static class ContentDirectoryExtensions
{
    /// <summary>Whether content exists at a path.</summary>
    /// <param name="directory">The content location.</param>
    /// <param name="path">The path relative to the location, with <c>/</c> separators.</param>
    /// <returns><c>true</c> when the content exists.</returns>
    public static bool Exists(this IContentDirectory directory, string path)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directory.Probe(path) is not null;
    }

    /// <summary>Reads all bytes of the content at a path.</summary>
    /// <param name="directory">The content location.</param>
    /// <param name="path">The path relative to the location, with <c>/</c> separators.</param>
    /// <returns>The content bytes.</returns>
    /// <exception cref="FileNotFoundException">No content exists at <paramref name="path"/>.</exception>
    public static byte[] ReadAllBytes(this IContentDirectory directory, string path)
    {
        ArgumentNullException.ThrowIfNull(directory);
        using var source = directory.Open(path);
        var bytes = new byte[checked((int)source.Length)];
        if (source.ReadAt(0, bytes) != bytes.Length)
        {
            throw new EndOfStreamException($"Content '{path}' ended before its declared length.");
        }

        return bytes;
    }

    /// <summary>Reads the content at a path as UTF-8 text, honoring a byte order mark.</summary>
    /// <param name="directory">The content location.</param>
    /// <param name="path">The path relative to the location, with <c>/</c> separators.</param>
    /// <returns>The text.</returns>
    /// <exception cref="FileNotFoundException">No content exists at <paramref name="path"/>.</exception>
    public static string ReadAllText(this IContentDirectory directory, string path)
    {
        using var reader = new StreamReader(new MemoryStream(directory.ReadAllBytes(path)), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
