namespace Turian;

/// <summary>
/// Saves a member typed as a node, component or DataAsset as an inline copy of the object instead of a reference
/// to it.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SerializeInlineAttribute : Attribute;
