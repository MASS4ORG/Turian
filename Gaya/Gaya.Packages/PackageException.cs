namespace Gaya.Packages;

/// <summary>A package, manifest or lock file that cannot be read, fetched or resolved.</summary>
/// <param name="message">What went wrong, naming the package and file involved.</param>
/// <param name="inner">The underlying failure, if any.</param>
public sealed class PackageException(string message, Exception? inner = null) : Exception(message, inner);
