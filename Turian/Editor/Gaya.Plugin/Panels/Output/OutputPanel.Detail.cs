namespace Gaya.Plugin.Turian;

sealed partial class OutputPanel
{
    TextEditState? detailState;
    RichText? detailRich;
    (int Raw, string Text) detailKey = (-1, "");
    IReadOnlyList<LogLink> detailLinks = [];

    /// <summary>
    /// The message pane under the entries list. Always present: with an entry selected it shows the whole
    /// message wrapped and scrollable, each stack frame that names an existing project file as a link that opens
    /// the file, and a drag highlights text for Ctrl+C. Right-click offers Copy. The scroll state and the text
    /// selection are keyed by the selected row, so picking another message starts at its top with nothing
    /// selected.
    /// </summary>
    void Detail(Gui gui)
    {
        var size = Theme.Text(12);
        var id = selectedIndex >= 0 ? $"{detailNodeId}/{rows[selectedIndex].RawIndex}" : detailNodeId;

        using (gui.Node(-1, -1, id).Expand().Enter())
        {
            gui.ScrollY();

            if (selectedIndex < 0 || selectedIndex >= rows.Count)
            {
                detailState = null;
                return;
            }

            var row = rows[selectedIndex];
            var viewport = gui.CurrentNode.InnerRect;
            using (gui.Node().ExpandWidth().Enter())
            {
                var result = gui.DrawRichText(DetailText(row), TextLayoutOptions.FillWidth, size, Theme.Ink,
                    settings.Monospace ? Mono(size) : null);
                _ = result.Node.ExpandWidth();
                var state = TextEditor.State(gui, $"{id}/text", row.Line.Text);
                detailState = state;
                if (gui.Pass == Pass.Pass2Render)
                {
                    HandleDetailInput(gui, result, row, state, viewport);
                }
            }
        }
    }

    /// <summary>
    /// The message as rich text, rebuilt only when another entry is shown: the link ranges come from checking
    /// the trace's files on disk, which is too much work to repeat every frame.
    /// </summary>
    RichText DetailText(LogRow row)
    {
        var key = (row.RawIndex, row.Line.Text);
        if (detailRich is not null && detailKey == key)
        {
            return detailRich;
        }

        detailKey = key;
        detailLinks = LogSource.Links(row.Line.Text, settingsService.Settings?.ProjectAbsoluteDir);
        var rich = new RichText();
        var linkStyle = new TextSpanStyle { Color = Theme.Info, Decoration = TextDecoration.Underline };
        var position = 0;
        foreach (var link in detailLinks)
        {
            _ = rich.Append(row.Line.Text[position..link.Start]);
            _ = rich.AppendLink(row.Line.Text.Substring(link.Start, link.Length), link.Line, linkStyle);
            position = link.Start + link.Length;
        }

        _ = rich.Append(row.Line.Text[position..]);
        return detailRich = rich;
    }

    /// <summary>
    /// A press on a link opens its file; any other press inside the pane anchors a text selection that a drag
    /// extends and a release ends; a right press opens the Copy menu. The selection is highlighted over the text.
    /// </summary>
    void HandleDetailInput(Gui gui, RichTextResult result, LogRow row, TextEditState state, Rect viewport)
    {
        var inner = result.Node.InnerRect;
        var origin = new Vector2(inner.X, inner.Y);
        var at = result.Layout.OffsetAt(gui.LocalMousePosition - origin);

        if (result.Clicked is { } link)
        {
            LogSource.Open(link.Target, log, settingsService.Settings?.ProjectAbsoluteDir);
        }
        else if (gui.Input.IsMouseButtonPressed(MouseButton.Left) && gui.IsPointerIn(viewport))
        {
            state.MoveTo(at, extend: false);
            state.IsSelecting = true;
        }
        else if (state.IsSelecting && gui.Input.IsMouseButtonDown(MouseButton.Left))
        {
            state.MoveTo(at, extend: true);
        }
        else if (state.IsSelecting)
        {
            state.IsSelecting = false;
        }

        if (gui.Input.IsMouseButtonPressed(MouseButton.Right) && gui.IsPointerIn(viewport))
        {
            OpenMenu(gui, row.Line, state.HasSelection ? state.SelectedText : row.Line.Text);
        }

        if (!state.HasSelection)
        {
            return;
        }

        foreach (var rect in result.Layout.RangeBounds(state.SelectionStart, state.SelectionEnd))
        {
            gui.DrawRect(new Rect(origin.X + rect.X, origin.Y + rect.Y, rect.W, rect.H), Theme.Selection);
        }
    }
}
