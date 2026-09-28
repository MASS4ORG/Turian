namespace Gaya.Plugin.Turian;

/// <summary>Default value-type drawers shared by inspector rows and label-free settings controls.</summary>
static class BuiltinPropertyDrawers
{
    static readonly IPropertyDrawer boolean = new PrimitiveDrawer(
        (gui, field, _, _, _) => FieldDrawers.DrawBool(gui, field));
    static readonly IPropertyDrawer text = new PrimitiveDrawer(
        (gui, field, _, id, _) => FieldDrawers.DrawString(gui, field, id));
    static readonly IPropertyDrawer number = new PrimitiveDrawer(
        (gui, field, type, id, _) => FieldDrawers.DrawNumber(gui, field, type, id), numeric: true);
    static readonly IPropertyDrawer enumeration = new PrimitiveDrawer(
        (gui, field, type, id, translate) => FieldDrawers.DrawEnum(gui, field, type, id, translate));
    internal static readonly IPropertyDrawer Summary = new PrimitiveDrawer(
        (gui, field, _, _, _) => gui.DrawText(field.GetValue()?.ToString() ?? "—",
            StudioTheme.Current.Text(12), StudioTheme.Current.InkDim, centerInRect: false));

    public static IPropertyDrawer For(Type type) =>
        type == typeof(bool) ? boolean
        : type == typeof(string) ? text
        : type.IsEnum ? enumeration
        : FieldDrawers.IsNumeric(type) ? number
        : Summary;

    sealed class PrimitiveDrawer(
        Action<Gui, FormField, Type, string, Func<string, string>?> draw, bool numeric = false)
        : IPropertyDrawer
    {
        public void Draw(Gui gui, FormField field, string id)
        {
            var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
            FieldDrawers.Row(gui, field.Label, id, () => DrawValue(gui, field, id),
                numeric && !field.IsReadOnly ? () => FieldDrawers.ScrubLabel(gui, field, type) : null);
        }

        public bool DrawValue(Gui gui, FormField field, string id) =>
            DrawValue(gui, field, id, null);

        public bool DrawValue(Gui gui, FormField field, string id, Func<string, string>? translate)
        {
            var type = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
            if (field.IsReadOnly && !ReferenceEquals(this, Summary))
                Summary.DrawValue(gui, field, id);
            else
                draw(gui, field, type, id, translate);
            return true;
        }
    }
}
