namespace Gaya.Plugin.Turian;

/// <summary>
/// How Turian's panels render forms: Studio's sizes and glyphs on top of the control palette the workbench already
/// applies, the plugin's own custom editors, and engine objects kept out of inline editing.
/// </summary>
static class TurianForms
{
    /// <summary>The plugin's custom editors (<c>[CustomEditor]</c> in this assembly) over Guinevere's defaults.</summary>
    public static FormDrawers Drawers { get; } = CreateDrawers();

    /// <summary>
    /// Sets Studio's row height, font size, label width, indent and fold glyphs on the current node, so every form
    /// drawn beneath it matches the theme. Call once per frame inside the panel's root node.
    /// </summary>
    public static void ApplyStyle(Gui gui)
    {
        var theme = ThemeTokens.Current;
        gui.CurrentNodeScope.Set(new ILayoutNodeScopeValue[]
        {
            ControlStyles.Value<ControlCompactHeight, float>(theme.Scale(theme.RowHeight)),
            ControlStyles.Value<ControlCompactFontSize, float>(theme.Text(12)),
            ControlStyles.Value<FormLabelWidth, float>(theme.Scale(96f)),
            ControlStyles.Value<FormIndent, float>(theme.Scale(12f)),
            ControlStyles.Value<FormCaretOpen, string>(EditorIcons.CaretDown),
            ControlStyles.Value<FormCaretClosed, string>(EditorIcons.CaretRight),
        });
    }

    /// <summary>A node, component or asset is referenced, never edited in place inside another object's form.</summary>
    public static bool CanInline(Type type) =>
        !typeof(IdObject).IsAssignableFrom(type) && !typeof(Asset).IsAssignableFrom(type);

    static FormDrawers CreateDrawers()
    {
        var drawers = new FormDrawers();
        drawers.AddCustomEditors(typeof(TurianForms).Assembly);
        return drawers;
    }
}
