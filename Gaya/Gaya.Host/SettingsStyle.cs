namespace Gaya.Host;

/// <summary>Projects the studio theme onto the controls the Settings panel draws with.</summary>
static class SettingsStyle
{
    /// <summary>The Font Awesome rotate-left glyph, which the workbench font set includes.</summary>
    public const string RevertIcon = "";

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <summary>The category tree's metrics, matching the studio's other trees.</summary>
    public static TreeViewTheme Tree() => new()
    {
        RowHeight = Theme.Scale(Theme.RowHeight),
        IndentWidth = Theme.Scale(14f),
        FontSize = Theme.Text(Theme.FontSize),
        ExpanderWidth = Theme.Scale(10f),
        IconSize = Theme.Scale(12f),
    };

    /// <summary>
    /// Sets the theme's row height, font size, label width and indent on the current node, under the control
    /// palette the workbench already applies, so the form editors beneath it match the rest of the studio.
    /// </summary>
    public static void ApplyFormStyle(Gui gui)
    {
        gui.CurrentNodeScope.Set(new ILayoutNodeScopeValue[]
        {
            ControlStyles.Value<ControlCompactHeight, float>(Theme.Scale(Theme.RowHeight)),
            ControlStyles.Value<ControlCompactFontSize, float>(Theme.Text(12)),
            ControlStyles.Value<FormLabelWidth, float>(Theme.Scale(96f)),
            ControlStyles.Value<FormIndent, float>(Theme.Scale(12f)),
        });
    }
}
