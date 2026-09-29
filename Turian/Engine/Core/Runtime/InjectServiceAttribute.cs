namespace Turian.Engine.Core;

/// <summary>
/// Injects a registered service into a scene object before its lifecycle callbacks.
/// Mark injected properties with <see cref="JsonIgnoreAttribute"/> so they are not persisted in scenes.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class InjectServiceAttribute : Attribute;
