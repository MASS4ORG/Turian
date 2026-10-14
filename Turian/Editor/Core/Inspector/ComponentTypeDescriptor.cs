// Turian.Editor.Core/Inspector/ComponentTypeDescriptor.cs
namespace Turian.Editor.Core;

/// <summary>
/// Lightweight, UI-agnostic descriptor for a <see cref="Component"/> type
/// that can be added to a node. Replaces the Studio-only
/// <c>ComponentTypeItemViewModel</c> for use in Core consumers.
/// </summary>
public sealed class ComponentTypeDescriptor
{
    /// <param name="componentType">The concrete component type to describe.</param>
    public ComponentTypeDescriptor(Type componentType)
    {
        ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
    }

    /// <summary>The reflected <see cref="Type"/>.</summary>
    public Type ComponentType { get; }

    /// <summary>Short human-readable name (last menu-path segment).</summary>
    public string DisplayName => ComponentRegistry.GetDisplayName(ComponentType);

    /// <summary>Full assembly-qualified name, for serialisation.</summary>
    public string FullName => ComponentType.FullName ?? ComponentType.Name;

    /// <summary>The <see cref="ComponentContextMenuAttribute"/> path, used for grouping/sorting.</summary>
    public string MenuPath => ComponentRegistry.GetMenuPath(ComponentType);

    /// <inheritdoc/>
    public override string ToString() => MenuPath;
}
