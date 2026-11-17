namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    const int countCap = 999;

    /// <summary>
    /// A severity filter as an icon button with its entry count: pressed (filled) while the category is shown, faded
    /// while hidden, named by a tooltip.
    /// </summary>
    void SeverityToggle(Gui gui, string key, string icon, int count, string label, ref bool shown)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);
        using (gui.Node(-1, height, $"{toggleId}/{key}").Direction(Axis.Horizontal).Gap(4f).Padding(6f, 0f)
                   .ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();
            if (gui.Pass == Pass.Pass2Render)
            {
                if (shown)
                {
                    gui.DrawBackgroundRect(Theme.AccentFill, 3f);
                }
                else if (hot)
                {
                    gui.DrawBackgroundRect(Theme.Hover, 3f);
                }
            }

            _ = gui.ThemedIcon(icon, Theme.Scale(14f), shown ? 1f : 0.4f);
            gui.DrawText(CountLabel(count), Theme.Text(11f), shown ? Theme.Ink : Theme.InkDim);
            gui.Tooltip(gui.CurrentNode, label);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
            {
                shown = !shown;
            }
        }
    }

    /// <summary>A count as shown beside a toggle: exact up to 999, then capped so the toggle keeps its width.</summary>
    internal static string CountLabel(int count) => count > countCap
        ? $"{countCap.ToString(CultureInfo.InvariantCulture)}+"
        : count.ToString(CultureInfo.InvariantCulture);
}
