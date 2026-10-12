namespace Turian.Engine.Core;

/// <summary>
/// Identifies one piece of content and the revision that was opened, so caches and mount sets can tell whether the
/// bytes behind a name changed without re-reading them.
/// </summary>
/// <param name="Id">A name that is stable for the content's location, such as an absolute path or an APK asset path.</param>
/// <param name="Version">An opaque revision token; two identities with the same id and version hold the same bytes.</param>
public readonly record struct ContentIdentity(string Id, string Version);

/// <summary>
/// A read-only, random-access blob of runtime content: a packaged file, an APK asset or a downloaded package.
/// The source owns its backing handle; positional reads are safe from any number of threads at once.
/// </summary>
public interface IContentSource : IDisposable
{
    /// <summary>The identity of the content as it was when the source was opened.</summary>
    ContentIdentity Identity { get; }

    /// <summary>The content length in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Reads bytes at an absolute offset without touching any shared position, so concurrent calls never interfere.
    /// </summary>
    /// <param name="offset">The offset from the start of the content.</param>
    /// <param name="buffer">The destination; reading stops at its length or at the end of the content.</param>
    /// <returns>The number of bytes read; <c>0</c> at or past the end of the content.</returns>
    int ReadAt(long offset, Span<byte> buffer);

    /// <summary>
    /// Opens an independent, seekable, read-only stream over a range of the content. The stream borrows the source:
    /// disposing the stream leaves the source open, and the stream throws once the source is disposed.
    /// </summary>
    /// <param name="offset">The range start from the start of the content.</param>
    /// <param name="length">The range length in bytes.</param>
    /// <returns>A stream the caller disposes; each call returns a stream with its own position.</returns>
    Stream OpenRead(long offset, long length);
}

/// <summary>
/// A read-only location holding named content, such as a folder beside the game or the APK's assets, which
/// enumerates overlays and opens content by relative path.
/// </summary>
public interface IContentDirectory
{
    /// <summary>A stable name unique to the location, used in identities, mount caches and diagnostics.</summary>
    string Id { get; }

    /// <summary>Gets the current identity of content without opening it, so a mount set can detect changes.</summary>
    /// <param name="path">The path relative to the location, with <c>/</c> separators.</param>
    /// <returns>The identity, or <c>null</c> when no such content exists.</returns>
    ContentIdentity? Probe(string path);

    /// <summary>Opens content for reading; the caller owns and disposes the returned source.</summary>
    /// <param name="path">The path relative to the location, with <c>/</c> separators.</param>
    /// <returns>The opened source.</returns>
    /// <exception cref="FileNotFoundException">No content exists at <paramref name="path"/>.</exception>
    IContentSource Open(string path);

    /// <summary>Lists the content directly inside a folder whose name ends with an extension.</summary>
    /// <param name="folder">The folder relative to the location, with <c>/</c> separators; empty for the root.</param>
    /// <param name="extension">The extension to match, including the dot, such as <c>.oap</c>.</param>
    /// <returns>Relative paths sorted ordinally ignoring case; empty when the folder does not exist.</returns>
    IReadOnlyList<string> List(string folder, string extension);
}
