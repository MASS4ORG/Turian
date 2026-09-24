namespace Gaya.Plugin.Turian;

/// <summary>
/// A colour as a swatch and four byte fields, one per channel, taken straight from the
/// dispatcher's hard-coded case and re-registered as a <see cref="CustomEditorAttribute"/> so it
/// serves both the inspector's labelled rows and the settings panel's label-free rows.
/// </summary>
[CustomEditor(typeof(GuiColor))]
sealed class ColorValueEditor : IValueEditor
{
    static StudioTheme Theme => StudioTheme.Current;

    static float RowHeight => Theme.Scale(Theme.RowHeight);
    static GuiColor Border => Theme.Border;

    public void Draw(Gui gui, FormField field, string id) =>
        FieldDrawers.Row(gui, field.Label, id, () => DrawValue(gui, field, id));

    public bool DrawValue(Gui gui, FormField field, string id)
    {
        var color = field.GetValue() is GuiColor value ? value : GuiColor.Black;

        using (gui.Node(RowHeight, RowHeight, $"{id}/swatch").Enter())
            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(color, 2);
                gui.DrawRectBorder(gui.CurrentNode.Rect, Border, 1, 2);
            }

        Span<int> parts = [color.R, color.G, color.B, color.A];
        var changed = false;

        for (var i = 0; i < 4; i++)
        {
            if (!FieldDrawers.TryEdit(gui, parts[i], $"{id}/c{i}", integral: true, 0, 255, out var next)) continue;

            parts[i] = (int)next;
            changed = true;
        }

        if (changed) field.SetValue(GuiColor.FromArgb(parts[3], parts[0], parts[1], parts[2]));
        return true;
    }
}
