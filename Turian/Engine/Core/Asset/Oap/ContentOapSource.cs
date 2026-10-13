namespace Turian.Engine.Core;

/// <summary>Lets the engine-independent <see cref="OapReader"/> read through an <see cref="IContentSource"/>.</summary>
/// <param name="content">The package content.</param>
/// <param name="ownsContent">Whether disposing the adapter disposes <paramref name="content"/>.</param>
sealed class ContentOapSource(IContentSource content, bool ownsContent) : IOapSource, IDisposable
{
    /// <inheritdoc />
    public long Length => content.Length;

    /// <inheritdoc />
    public int ReadAt(long offset, Span<byte> buffer) => content.ReadAt(offset, buffer);

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsContent) content.Dispose();
    }
}

/// <summary>Opens Open Asset Packages over engine content sources.</summary>
public static class OapContentReaders
{
    extension(OapReader)
    {
        /// <summary>Opens a package over an engine content source, reading only its metadata sections.</summary>
        /// <param name="source">The package content; it must stay open while the reader is used.</param>
        /// <param name="ownsSource">Whether disposing the reader disposes <paramref name="source"/>.</param>
        /// <returns>An open reader.</returns>
        public static OapReader Open(IContentSource source, bool ownsSource = false)
        {
            ArgumentNullException.ThrowIfNull(source);
            return OapReader.Open(new ContentOapSource(source, ownsSource), ownsSource: true);
        }
    }
}
