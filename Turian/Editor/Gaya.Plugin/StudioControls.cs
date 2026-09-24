namespace Gaya.Plugin.Turian;

/// <summary>
/// The palettes the studio hands to Guinevere's compound controls. They carry their own colors and
/// metrics rather than reading <see cref="StudioTheme"/>, so every tree and tab strip in the studio
/// is themed from one place instead of each panel repeating the projection.
/// </summary>
static class StudioControls
{
    static StudioTheme Theme => StudioTheme.Current;

    /// <summary>The tree palette shared by the scene tree, the asset browser and the settings list.</summary>
    public static TreeViewTheme Tree() => new()
    {
        RowHeight = Theme.Scale(Theme.RowHeight),
        IndentWidth = Theme.Scale(14f),
        FontSize = Theme.Text(Theme.FontSize),
        ExpanderWidth = Theme.Scale(10f),
        IconSize = Theme.Scale(12f),
    };

    /// <summary>The tab-strip palette for the document tabs.</summary>
    /// <param name="height">Strip height, which the chrome also reports to the workbench.</param>
    public static TabStripTheme Tabs(float height) => new()
    {
        Height = height,
        FontSize = Theme.Text(Theme.FontSize),
        Strip = Theme.Chrome,
        Active = Theme.Panel,
        Tab = Theme.Background,
        Hover = Theme.Hover,
        Ink = Theme.Ink,
        InkDim = Theme.InkDim,
        Accent = Theme.Accent,
        IconSize = Theme.Scale(12f),
    };

    /// <summary>A short labelled button sized to a row. Blocks input, so a click never reaches behind it.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="label">The text drawn in the button.</param>
    /// <param name="id">A unique id for the button's node.</param>
    /// <param name="width">The button's width in scaled pixels.</param>
    /// <returns>True on the frame the button was clicked.</returns>
    public static bool SmallTextButton(Gui gui, string label, string id, float width)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var height = Theme.Scale(Theme.RowHeight);

        using (gui.Node(width, height, id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(hot ? Theme.Hover : Theme.Chrome, 3f);
            gui.DrawText(label, Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim);

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }
}
