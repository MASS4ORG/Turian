namespace Gaya.Packages.Editor;

public sealed partial class BricksPanel
{
    /// <summary>Highlights the row being drawn and runs <paramref name="select"/> when it is clicked.</summary>
    void Selectable(Gui gui, bool selected, Action select)
    {
        var interactable = gui.GetInteractable();
        if (gui.Pass != Pass.Pass2Render)
        {
            return;
        }

        if (selected)
        {
            gui.DrawBackgroundRect(Theme.Hover, 3f);
        }
        else if (interactable.OnHover())
        {
            gui.DrawBackgroundRect(Theme.Chrome, 3f);
        }

        if (interactable.OnClick())
        {
            Show(select);
        }
    }

    /// <summary>A chip that opens a menu: its label and a caret.</summary>
    static bool DropdownChip(Gui gui, string label, string id, float width, bool active)
    {
        using (gui.Node(Theme.Scale(width), Theme.Scale(Theme.RowHeight), id).BlockInput().Direction(Axis.Horizontal)
                   .Gap(4f).ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();
            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(active ? Theme.Hover : hot ? Theme.Chrome : Theme.Panel, 9f);
            }

            using (gui.Node().Enter())
            {
                gui.DrawText(label, Theme.Text(11f), active || hot ? Theme.Ink : Theme.InkDim);
            }

            _ = gui.Icon(gui.ResolveIcon(Icons.CaretDown), Theme.Text(9), null, 0.7f);
            return gui.Pass == Pass.Pass2Render && interactable.OnClick();
        }
    }

    static void Line(Gui gui, string text, Color color)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight - 6f), $"bricks/line/{text}").ExpandWidth().ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
        }
    }

    static void Cell(Gui gui, string id, string text, float width, float height, Color? color = null)
    {
        using (gui.Node(Theme.Scale(width), height, id).ContentAlignY(0.5f).Enter())
        {
            gui.ClipContent();
            gui.DrawText(text, Theme.Text(11f), color ?? Theme.InkDim, centerInRect: false);
        }
    }

    static bool Chip(Gui gui, string label, string id, float width, bool active)
    {
        var height = Theme.Scale(Theme.RowHeight);

        using (gui.Node(Theme.Scale(width), height, id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(active ? Theme.Hover : hot ? Theme.Chrome : Theme.Panel, 9f);
            }

            gui.DrawText(label, Theme.Text(11f), active || hot ? Theme.Ink : Theme.InkDim);

            return gui.Pass == Pass.Pass2Render && interactable.OnClick();
        }
    }

    /// <summary>A text button; <paramref name="accent"/> fills it with the accent colour, for the one action to take next.</summary>
    static bool Button(Gui gui, Frame now, string label, string id, float width, string? tooltip = null, bool accent = false) =>
        SmallTextButton(gui, label, id, Theme.Scale(width), tooltip, accent) && !now.Busy;

    static bool SmallTextButton(Gui gui, string label, string id, float width, string? tooltip, bool accent = false)
    {
        using (gui.Node(width, Theme.Scale(Theme.RowHeight), id).BlockInput().ContentAlignX(0.5f).ContentAlignY(0.5f)
                   .Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(accent ? hot ? Theme.AccentHover : Theme.Accent : hot ? Theme.Hover : Theme.Chrome, 3f);
            }

            gui.DrawText(label, Theme.Text(11f), accent ? Theme.InkOnAccent : hot ? Theme.Ink : Theme.InkDim);
            if (tooltip is not null)
            {
                gui.Tooltip(gui.CurrentNode, tooltip, maxWidth: 320);
            }

            return gui.Pass == Pass.Pass2Render && hot && interactable.OnClick();
        }
    }
}
