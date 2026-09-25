namespace Gaya.Plugin.Turian;

/// <summary>
/// The console: the tail of the log buffer under a toolbar. A filter box narrows the
/// messages, three toggles gate the error/warning/debug lanes, a Collapse toggle merges consecutive
/// duplicates behind a count badge, and the list follows the tail as long as it is pinned to the
/// bottom. The entries list and its message detail sit above and below a drag divider, the detail
/// always visible even with nothing selected. The detail shows the whole message — the stack traces
/// the list only shows a line or two of — wrapped and scrollable, and is text-selectable: a drag
/// highlights a run and Ctrl+C copies just that, while a click copies the whole entry. Clicking a row
/// selects it, a double click jumps to its source, and the row and detail jump together to wherever
/// the split leaves them. The preferences live in the … menu on the tab strip
/// (<c>OutputPanelChrome</c>), not in a settings row of their own.
/// </summary>
sealed class OutputPanel(
    ILogger log,
    OutputPanelSettings settings,
    OutputLogBridge bridge,
    SettingsService settingsService,
    IFocusTracker focusTracker,
    StudioLocalization localization) : IPanel
{
    const string scrollId = "gaya.turian.output/log";
    const string detailNodeId = "gaya.turian.output/detail";
    const string headerId = "gaya.turian.output/header";
    const string splitId = "gaya.turian.output/split";
    const string filterId = "gaya.turian.output/filter";
    const string clearId = "gaya.turian.output/clear";
    const string rowPrefix = "gaya.turian.output/row/";
    const float rowHeight = 16f;
    const float defaultSplit = 0.68f;

    string filter = "";
    bool collapse;
    bool showErrors = true;
    bool showWarnings = true;
    bool showLog = true;

    int selectedIndex = -1;
    bool followTail = true;
    float priorTailBottom;
    float split = defaultSplit;
    long lastGeneration;

    Font? monoFont;
    SKFont? monoMeasure;
    float monoSize;

    IReadOnlyList<LogRow> rows = [];

    static StudioTheme Theme => StudioTheme.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        if (bridge.Generation != lastGeneration)
        {
            lastGeneration = bridge.Generation;
            selectedIndex = -1;
            followTail = true;
        }

        Navigate(gui);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter())
        {
            Header(gui);

            using (gui.Node(-1, -1, splitId).Expand().Direction(Axis.Vertical).Enter())
            {
                using (gui.Node().ExpandWidth().ExpandHeight(split).Enter())
                    Entries(gui);

                gui.Splitter(ref split, Axis.Vertical,
                    thickness: Theme.Scale(2f), min: 0.25f,
                    color: Theme.Border, hoverColor: Theme.Hover);

                using (gui.Node().ExpandWidth().ExpandHeight(1f - split).Enter())
                    Detail(gui);
            }
        }
    }

    /// <summary>The filter box and the Collapse/severity toggles. The Clear button empties the log.</summary>
    void Header(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);
        var box = Theme.Scale(13f);

        using (gui.Node(-1, height, headerId).ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(10f).ContentAlignY(0.5f).Enter())
        {
            gui.Checkbox(ref collapse, localization.T("Collapse"), size: box, fontSize: Theme.Text(11f), spacing: 4f);
            gui.Checkbox(ref showErrors, localization.T("Errors"), size: box, fontSize: Theme.Text(11f), spacing: 4f);
            gui.Checkbox(ref showWarnings, localization.T("Warnings"), size: box, fontSize: Theme.Text(11f), spacing: 4f);
            gui.Checkbox(ref showLog, localization.T("Debug"), size: box, fontSize: Theme.Text(11f), spacing: 4f);

            filter = gui.TextInput(filter, width: 0, height: height, placeholder: localization.T("Filter output"),
                fontSize: Theme.Text(12), padding: 5, id: filterId);

            if (StudioControls.SmallTextButton(gui, localization.T("Clear"), clearId, Theme.Scale(62f)))
            {
                LogBuffer.Clear();
                selectedIndex = -1;
                followTail = true;
            }
        }
    }

    void Entries(Gui gui)
    {
        using (gui.Node(-1, -1, scrollId).Expand().Enter())
        {
            gui.ScrollY();

            var snapshot = LogBuffer.Snapshot();
            rows = LogView.Project(snapshot, filter, showErrors, showWarnings, showLog, collapse);
            if (selectedIndex >= rows.Count) selectedIndex = -1;

            for (var index = 0; index < rows.Count; index++)
                Row(gui, rows[index], index);

            if (rows.Count == 0 && snapshot.Count > 0)
                gui.DrawText("No log messages match the filter.", Theme.Text(11f), Theme.InkDim,
                    centerInRect: false);

            if (gui.Pass == Pass.Pass2Render) FollowTail(gui);
        }
    }

    /// <summary>
    /// Keyboard driving of the entries list. Fires only once the Output panel holds the studio focus
    /// and no text field is editing (the filter box), so typing in the filter cannot walk the
    /// selection. Up/Down move by one row, Page up/down by a viewport, Home/End jump to the start or
    /// the newest message, and the selected row is scrolled into view. Selecting the newest message
    /// re-engages the tail-follower; moving away from it disengages it, exactly like dragging the
    /// scrollbar does.
    /// </summary>
    void Navigate(Gui gui)
    {
        if (gui.Pass != Pass.Pass2Render || rows.Count == 0) return;
        if (focusTracker.ActivePanelId != GayaPlugin.OutputPanelId) return;
        if (gui.Focus.IsTextInputFocused) return;

        var page = Math.Max(1, PageSize(gui));
        if (gui.Input.IsKeyPressed(KeyboardKey.Up)) MoveSelection(-1, gui);
        else if (gui.Input.IsKeyPressed(KeyboardKey.Down)) MoveSelection(1, gui);
        else if (gui.Input.IsKeyPressed(KeyboardKey.PageUp)) MoveSelection(-page, gui);
        else if (gui.Input.IsKeyPressed(KeyboardKey.PageDown)) MoveSelection(page, gui);
        else if (gui.Input.IsKeyPressed(KeyboardKey.Home)) MoveSelection(int.MinValue, gui);
        else if (gui.Input.IsKeyPressed(KeyboardKey.End)) MoveSelection(int.MaxValue, gui);
    }

    /// <summary>
    /// Moves the selected row by <paramref name="delta"/> rows, or to the first/last row for the
    /// sentinel values, clamping to the list, then scrolls it into view.
    /// </summary>
    void MoveSelection(int delta, Gui gui)
    {
        var start = selectedIndex < 0 ? (delta > 0 ? -1 : 0) : selectedIndex;
        var target = delta == int.MinValue ? 0
            : delta == int.MaxValue ? rows.Count - 1
            : Math.Clamp(start + delta, 0, rows.Count - 1);
        if (target == selectedIndex) return;

        selectedIndex = target;
        followTail = selectedIndex == rows.Count - 1;
        RevealRow(gui, selectedIndex);
    }

    /// <summary>Scrolls the selected row into view, keeping a little breathing room at each edge.</summary>
    void RevealRow(Gui gui, int index)
    {
        var scroll = gui.GetScrollState(scrollId);
        if (scroll is null) return;

        var top = 0f;
        for (var i = 0; i < index; i++) top += RowHeight(rows[i]);
        var bottom = top + RowHeight(rows[index]);
        var offset = scroll.ScrollOffset.Y;

        if (top < offset) offset = MathF.Max(0f, top - 4f);
        else if (bottom > offset + scroll.ViewportSize.Y) offset = bottom - scroll.ViewportSize.Y + 4f;
        else return;

        gui.ScrollBy(scrollId, new Vector2(0f, offset - scroll.ScrollOffset.Y));
    }

    /// <summary>The height one list entry occupies: its timestamp/text block, clamped to the minimum row.</summary>
    float RowHeight(LogRow row)
    {
        var size = Theme.Text(11);
        var text = LogView.FirstLines(row.Line.Text, Math.Max(1, settings.EntryLines));
        var shownLines = text.Count(character => character == '\n') + 1;
        return Math.Max(Theme.Scale(rowHeight), Theme.Scale(size * 1.2f * shownLines));
    }

    /// <summary>How many rows a full page of the list shows, for Page up/down.</summary>
    int PageSize(Gui gui)
    {
        var scroll = gui.GetScrollState(scrollId);
        if (scroll is null) return 1;
        var row = rows[Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, rows.Count - 1)];
        return Math.Max(1, (int)(scroll.ViewportSize.Y / Math.Max(1f, RowHeight(row))));
    }

    void Row(Gui gui, LogRow row, int index)
    {
        var size = Theme.Text(11);
        var text = LogView.FirstLines(row.Line.Text, Math.Max(1, settings.EntryLines));
        var shownLines = text.Count(character => character == '\n') + 1;
        var lineHeight = size * 1.2f;
        var height = Math.Max(Theme.Scale(rowHeight), Theme.Scale(lineHeight * shownLines));
        var font = settings.Monospace ? Mono(size) : null;
        var id = $"{rowPrefix}{row.RawIndex}";

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hovered = interactable.OnHover();
            if (index == selectedIndex) gui.DrawBackgroundRect(Theme.AccentFill, 2f);
            else if (hovered) gui.DrawBackgroundRect(Theme.Hover, 2f);

            if (settings.ShowTimestamp)
                gui.DrawText(
                    row.Line.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    size, Theme.InkFaint, font);
            gui.DrawText(text, size, LevelColor(row.Line.Level), font);

            if (row.Count > 1)
            {
                using (gui.Node().Expand().Enter()) { }
                CountBadge(gui, row.Count, height);
            }

            if (gui.Pass == Pass.Pass2Render && hovered && interactable.OnClick(out var clicks))
            {
                if (clicks >= 2)
                {
                    selectedIndex = index;
                    LogSource.Open(row.Line.Text, log, settingsService.Settings?.ProjectAbsoluteDir);
                }
                else if (index == selectedIndex)
                {
                    // Clicking the selected row again dismisses its detail.
                    selectedIndex = -1;
                }
                else
                {
                    selectedIndex = index;
                }
            }
        }
    }

    /// <summary>
    /// The message pane under the entries list. Always present: selected, it shows the whole message
    /// wrapped and scrollable through <c>WrappedLabel</c>, which also handles drag-to-select and the
    /// copy shortcut; otherwise a hint. The scroll state and the text selection are keyed by the
    /// selected row, so picking another message starts at its top with nothing selected.
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
                return;
            }

            var row = rows[selectedIndex];
            gui.WrappedLabel(row.Line.Text, size, MeasureFont(gui, size), LevelColor(row.Line.Level),
                GuiColor.FromArgb(80, Theme.Accent), settings.Monospace ? Mono(size) : null);
        }
    }

    /// <summary>The number of duplicates a collapsed row stands for, in a small badge over the right edge.</summary>
    static void CountBadge(Gui gui, int count, float height)
    {
        using (gui.Node(Theme.Scale(22f), Math.Max(Theme.Scale(13f), height - Theme.Scale(3f)))
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(Theme.Chrome, 3f);
            gui.DrawText(count.ToString(CultureInfo.InvariantCulture), Theme.Text(10f), Theme.InkDim);
        }
    }

    /// <summary>
    /// Keeps the list pinned to the newest message as long as it was following the tail: new rows grow
    /// the content, and the following view re-anchors at the bottom. The moment the offset is dragged
    /// above the bottom the tail is lost, and it only comes back once the user scrolls down again.
    /// </summary>
    void FollowTail(Gui gui)
    {
        var state = gui.GetScrollState(scrollId);
        if (state is null) return;

        var max = state.MaxScroll.Y;
        var atBottom = MathF.Abs(max) < float.Epsilon
            || MathF.Abs(state.ScrollOffset.Y - max) < 1f;

        if (atBottom)
        {
            followTail = true;
        }
        else if (state.ScrollOffset.Y < priorTailBottom - 1f)
        {
            followTail = false;
        }

        if (followTail) gui.ScrollToBottom(scrollId);

        priorTailBottom = max;
    }

    /// <summary>The monospaced font for drawing, reused until the size changes.</summary>
    Font Mono(float size)
    {
        if (monoFont is null || MathF.Abs(size - monoSize) > float.Epsilon)
        {
            monoFont?.Dispose();
            monoFont = Font.FromFamilyName("monospace", size);
            monoSize = size;
        }

        return monoFont;
    }

    /// <summary>
    /// The monospaced font for measuring, a sibling of <see cref="Mono"/> the draw font has no public
    /// access to its SKFont. Kept in lockstep with the drawing size, so the wrap and the selection
    /// arithmetic measure the glyphs the same way the pane paints them.
    /// </summary>
    SKFont MonoMeasure(float size)
    {
        if (monoMeasure is null || MathF.Abs(size - monoSize) > float.Epsilon)
        {
            monoMeasure?.Dispose();
            monoMeasure = new SKFont(SKTypeface.FromFamilyName("monospace"), size);
        }

        return monoMeasure;
    }

    /// <summary>The font the wrapped lines are measured with: monospace, or whatever scope font draws.</summary>
    SKFont MeasureFont(Gui gui, float size) =>
        settings.Monospace ? MonoMeasure(size) : TextEditor.MeasuringFont(gui, size);

    static GuiColor LevelColor(LogLevel level) => level switch
    {
        LogLevel.Critical or LogLevel.Error => Theme.Error,
        LogLevel.Warning => Theme.Warning,
        LogLevel.Debug or LogLevel.Trace => Theme.InkDim,
        _ => Theme.Ink,
    };
}
