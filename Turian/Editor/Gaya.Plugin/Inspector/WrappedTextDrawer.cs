namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws a read-only text value that wraps onto as many lines as the inspector's width needs, so a long id,
/// description or path is never cut off. The text is selectable for copying.
/// </summary>
sealed class WrappedTextDrawer : IPropertyDrawer
{
    /// <summary>The shared drawer.</summary>
    public static readonly WrappedTextDrawer Instance = new();

    /// <summary>Whether the drawer takes a field: read-only text.</summary>
    /// <param name="field">The field.</param>
    /// <returns>True for a read-only string.</returns>
    public static bool Takes(FormField field) =>
        field.IsReadOnly && (Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType) == typeof(string);

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var theme = ThemeTokens.Current;
        var row = theme.Scale(theme.RowHeight);
        using (gui.Node(-1, -1, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        {
            using (gui.Node(theme.Scale(96f), row, $"{id}/label").AlignContentY(0.5f).Enter())
            {
                gui.DrawText(field.Label, theme.Text(12), theme.Ink, centerInRect: false);
            }

            // Padding centres a single line on the row like the other fields; longer text grows the row downward.
            var padding = Math.Max(0f, (row - theme.Text(12) * 1.35f) / 2f);
            using (gui.Node(-1, -1, $"{id}/editor").ExpandWidth().Padding(padding, 0f, padding, 0f).Enter())
            {
                _ = DrawValue(gui, field, id, context);
            }
        }
    }

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var theme = ThemeTokens.Current;
        var text = field.HasMixedValue ? "—" : field.GetValue() as string ?? "—";
        _ = gui.DrawText(text, selectable: true, theme.Text(12), theme.InkDim, wrapWidth: TextLayoutOptions.FillWidth,
            centerInRect: false).ExpandWidth();
        return true;
    }
}
