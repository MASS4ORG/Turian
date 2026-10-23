namespace MASS4.Bricks;

/// <summary>A package, manifest or lock file that cannot be read, fetched or resolved.</summary>
/// <param name="message">What went wrong, naming the package and file involved.</param>
/// <param name="inner">The underlying failure, if any.</param>
public sealed class PackageException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// When a resolution stopped on several bricks at once, one line per brick saying why it is missing; empty for a
    /// single failure, which <see cref="Exception.Message"/> describes.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}
