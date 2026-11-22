namespace Gaya.Plugin.Turian;

/// <summary>Shared controls for Turian Studio panels.</summary>
static class StudioControls
{
    static ThemeTokens Theme => ThemeTokens.Current;

    /// <summary>A short labelled button sized to a row. Blocks input, so a click never reaches behind it.</summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="label">The text drawn in the button.</param>
    /// <param name="id">A unique id for the button's node.</param>
    /// <param name="width">The button's width in scaled pixels.</param>
    /// <param name="tooltip">Text shown after hovering the button, or null for none.</param>
    /// <returns>True on the frame the button was clicked.</returns>
    public static bool SmallTextButton(Gui gui, string label, string id, float width, string? tooltip = null)
    {
        ArgumentNullException.ThrowIfNull(gui);

        var height = Theme.Scale(Theme.RowHeight);

        using (gui.Node(width, height, id).BlockInput().AlignContentX(0.5f).AlignContentY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(hot ? Theme.Hover : Theme.Chrome, 3f);
            }

            gui.DrawText(label, Theme.Text(11f), hot ? Theme.Ink : Theme.InkDim);
            if (tooltip is not null)
            {
                gui.Tooltip(gui.CurrentNode, tooltip, maxWidth: 320);
            }

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }
}
