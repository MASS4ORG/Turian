namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws <see cref="Vector3"/> and <see cref="Vector4"/> members: marked with <see cref="ColorUsageAttribute"/> they
/// get the color field, every other vector keeps Guinevere's X/Y/Z/W row, which this drawer asks the default scope
/// for because a type drawer outranks the field predicates that would otherwise choose by attribute.
/// </summary>
sealed class ColorVectorDrawer : IPropertyDrawer
{
    static readonly MethodInfo? TypeDrawer =
        typeof(FormDrawers).GetMethod("TypeDrawer", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The shared drawer.</summary>
    public static readonly ColorVectorDrawer Instance = new();

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context)
    {
        if (IsColor(field))
        {
            EngineColorDrawer.Instance.Draw(gui, field, id, context);
        }
        else
        {
            Default(field.ValueType)?.Draw(gui, field, id, context);
        }
    }

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context) => IsColor(field)
        ? EngineColorDrawer.Instance.DrawValue(gui, field, id, context)
        : Default(field.ValueType)?.DrawValue(gui, field, id, context) ?? false;

    static bool IsColor(FormField field) => field.Attribute<ColorUsageAttribute>() is not null;

    static IPropertyDrawer? Default(Type type) => TypeDrawer?.Invoke(FormDrawers.Default, [type]) as IPropertyDrawer;
}
