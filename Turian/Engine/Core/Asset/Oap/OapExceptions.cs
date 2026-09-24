namespace Turian.Engine.Core;

/// <summary>
/// Base type for every error raised while reading or writing an Open Asset Package.
/// </summary>
public class OapException : Exception
{
    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">The error description.</param>
    public OapException(string message) : base(message)
    {
    }

    /// <summary>Initialises a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying cause.</param>
    public OapException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The data does not start with the OAP magic bytes.</summary>
public sealed class OapBadMagicException() : OapException("Not an OAP package: magic bytes do not match.");

/// <summary>The package declares a major version newer than this implementation supports.</summary>
public sealed class OapUnsupportedVersionException(ushort major)
    : OapException($"Unsupported OAP major version {major}; this reader supports {OapFormat.FormatMajor}.")
{
    /// <summary>Gets the unsupported major version read from the header.</summary>
    public ushort Major { get; } = major;
}

/// <summary>A CRC-32 check failed, or a section or blob was structurally malformed.</summary>
public sealed class OapCorruptDataException : OapException
{
    /// <summary>Initialises a new instance with the specified message.</summary>
    /// <param name="message">The error description.</param>
    public OapCorruptDataException(string message) : base(message)
    {
    }

    /// <summary>Initialises a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying cause.</param>
    public OapCorruptDataException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The data is shorter than a declared structure, section, or blob range.</summary>
public sealed class OapTruncatedException(string message) : OapException(message);

/// <summary>An asset uses a compression codec this build cannot decode.</summary>
public sealed class OapUnsupportedCompressionException(OapCompression codec)
    : OapException($"Unsupported OAP compression codec '{codec}'.")
{
    /// <summary>Gets the codec that could not be decoded.</summary>
    public OapCompression Codec { get; } = codec;
}

/// <summary>An asset is encrypted but no decryption key was supplied.</summary>
public sealed class OapKeyRequiredException() : OapException("The asset is encrypted but no key was provided.");

/// <summary>No asset matched a lookup by id or virtual path.</summary>
public sealed class OapNotFoundException(string what) : OapException($"No OAP asset matched '{what}'.");
