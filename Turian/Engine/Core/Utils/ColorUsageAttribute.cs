namespace Turian.Engine.Core;

/// <summary>
/// Marks a <see cref="Vector3"/> or <see cref="Vector4"/> member as a color (X=R, Y=G, Z=B, W=A, each 0..1), so the
/// editor edits it with a color picker instead of loose number fields.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ColorUsageAttribute : Attribute;
