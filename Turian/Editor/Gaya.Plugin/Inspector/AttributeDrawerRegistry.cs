namespace Gaya.Plugin.Turian;

/// <summary>Maps metadata attributes to composable field decorators.</summary>
public static class AttributeDrawerRegistry
{
    static readonly ConditionalWeakTable<Type, IAttributeDrawer> Drawers = new();
    static readonly IAttributeDrawer Tooltip = new TooltipDrawer();

    /// <summary>Registers or replaces the decorator for an attribute type.</summary>
    public static void Register<TAttribute>(IAttributeDrawer drawer) where TAttribute : Attribute
    {
        ArgumentNullException.ThrowIfNull(drawer);
        var type = typeof(TAttribute);
        lock (Drawers)
        {
            Drawers.Remove(type);
            Drawers.Add(type, drawer);
        }
    }

    /// <summary>Removes an attribute decorator.</summary>
    public static void Unregister<TAttribute>() where TAttribute : Attribute =>
        Drawers.Remove(typeof(TAttribute));

    /// <summary>Composes matching decorators outside the value-type drawer.</summary>
    public static void Draw(Gui gui, FormField field, string id, Action inner)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(inner);

        var chain = field.Metadata?.Attributes
            .Select((attribute, index) =>
                (attribute, index, drawer: Find(attribute.GetType())))
            .Where(entry => entry.drawer is not null)
            .OrderBy(entry => entry.drawer!.Order)
            .ThenBy(entry => entry.index)
            .ToArray() ?? [];

        var next = inner;
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            var (attribute, _, drawer) = chain[i];
            var continuation = next;
            next = () => drawer!.Draw(gui, field, attribute, id, continuation);
        }

        next();
    }

    internal static IAttributeDrawer? Find(Type type)
    {
        for (var current = type; current is not null && typeof(Attribute).IsAssignableFrom(current);
             current = current.BaseType)
            if (Drawers.TryGetValue(current, out var drawer)) return drawer;
        return typeof(TooltipAttribute).IsAssignableFrom(type) ? Tooltip : null;
    }
}
