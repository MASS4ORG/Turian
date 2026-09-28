using System.Runtime.CompilerServices;

namespace Gaya.Plugin.Turian;

/// <summary>Explicit extensions take precedence over the existing type-registered value editors.</summary>
public static class PropertyDrawerRegistry
{
    static readonly ConditionalWeakTable<Type, IPropertyDrawer> drawers = new();

    /// <summary>Registers or replaces a drawer for an exact value type.</summary>
    public static void Register(Type valueType, IPropertyDrawer drawer)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(drawer);
        lock (drawers)
        {
            drawers.Remove(valueType);
            drawers.Add(valueType, drawer);
        }
    }

    /// <summary>Unregisters a drawer; built-in type editors remain available.</summary>
    public static void Unregister(Type valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        drawers.Remove(valueType);
    }

    /// <summary>Finds a registered or built-in drawer for a value type.</summary>
    public static IPropertyDrawer For(Type valueType) =>
        CustomFor(valueType) ?? BuiltinPropertyDrawers.For(valueType);

    /// <summary>Finds an extension or an existing registered value editor.</summary>
    internal static IPropertyDrawer? CustomFor(Type valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        for (var current = valueType; current is not null; current = current.BaseType)
        {
            if (drawers.TryGetValue(current, out var drawer)) return drawer;
            foreach (var face in current.GetInterfaces())
                if (drawers.TryGetValue(face, out drawer)) return drawer;
        }

        return ValueEditorRegistry.For(valueType);
    }
}
