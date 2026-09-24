namespace Turian.Engine.Core;

/// <summary>
/// Thrown when a <see cref="TypeIdAttribute"/> Guid cannot be resolved to a loaded type.
/// Separate from <see cref="JsonException"/> so callers can catch only missing-type errors
/// and handle them (e.g., create a <c>MissingComponent</c> stub) without swallowing real JSON errors.
/// </summary>
[PublicAPI]
public class UnresolvableTypeIdException : JsonException
{
    /// <summary>
    /// The unresolved type id.
    /// </summary>
    public Guid TypeId { get; }

    /// <summary>
    /// The source asset path, if known.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public UnresolvableTypeIdException(Guid typeId, string message) : base(message) => TypeId = typeId;
}
