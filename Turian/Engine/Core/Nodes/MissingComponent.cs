namespace Turian.Engine.Core;

/// <summary>
/// Stub component used when a <see cref="Component"/>'s <see cref="TypeIdAttribute"/> Guid
/// cannot be resolved to a loaded type (e.g., user-code assembly not yet compiled,
/// or the script was deleted). Allows the prefab/scene to load so the user can
/// manually remove the missing component.
/// </summary>
public class MissingComponent : Component
{
    /// <summary>
    /// The unresolved <see cref="TypeIdAttribute"/> Guid that triggered this stub.
    /// </summary>
    public Guid UnresolvedTypeId { get; set; }

    /// <summary>
    /// Original type name if preserved in the serialized payload, otherwise empty.
    /// </summary>
    public string OriginalTypeName { get; set; } = string.Empty;
}
