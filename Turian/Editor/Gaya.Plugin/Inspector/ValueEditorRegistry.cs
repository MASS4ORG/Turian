namespace Gaya.Plugin.Turian;

/// <summary>
/// Finds the <see cref="IValueEditor"/> registered for a value type by scanning the plugin's own
/// types once for <see cref="CustomEditorAttribute"/> and caching the match per type. Later parts can
/// widen the scan to other assemblies — user code, other plugins — by feeding the registry the
/// assemblies it should look at.
/// </summary>
static class ValueEditorRegistry
{
    static readonly ConcurrentDictionary<Type, IValueEditor?> editorsByType = new();
    static Dictionary<Type, IValueEditor>? byEditedType;

    static Dictionary<Type, IValueEditor> ByEditedType
    {
        get
        {
            if (byEditedType is not null) return byEditedType;

            var map = new Dictionary<Type, IValueEditor>();
            foreach (var type in typeof(ValueEditorRegistry).Assembly.GetTypes())
            {
                if (type.IsAbstract) continue;
                if (!typeof(IValueEditor).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IValueEditor editor) continue;

                // A class may declare several editors — one attribute per value type it draws.
                foreach (var attribute in type.GetCustomAttributes<CustomEditorAttribute>())
                    map[attribute.EditorType] = editor;
            }

            return byEditedType = map;
        }
    }

    /// <summary>
    /// The custom editor registered for a value type, its base class, or the first of its interfaces
    /// that has one; null when no editor is registered, in which case the dispatcher falls back to
    /// its built-in drawers.
    /// </summary>
    /// <param name="valueType">The type the field holds.</param>
    public static IValueEditor? For(Type valueType) =>
        editorsByType.GetOrAdd(valueType, static t =>
        {
            var map = ByEditedType;

            for (var current = t; current is not null; current = current.BaseType)
            {
                if (map.TryGetValue(current, out var editor)) return editor;
                foreach (var face in current.GetInterfaces())
                    if (map.TryGetValue(face, out editor)) return editor;
            }

            return null;
        });
}
