namespace Gaya.Plugin.Turian;

/// <summary>
/// The console: the tail of the log buffer under a toolbar. A filter box narrows the
/// messages, independent severity toggles control visibility, a Collapse toggle merges consecutive
/// duplicates behind a count badge, and the list follows the tail as long as it is pinned to the
/// bottom. Each entry carries an icon for its severity. The entries list and its message detail sit above
/// and below a drag divider, the detail always visible even with nothing selected. Click, Ctrl+click and
/// Shift+click select entries, Ctrl+C or the context menu's Copy puts the whole text of every selected
/// entry on the clipboard, and a double click jumps to the source. The detail shows the whole message — the
/// stack traces the list only shows a line or two of — wrapped and scrollable, with each frame that names a
/// project file as a link; a drag highlights a run for Ctrl+C. The preferences, Studio messages among
/// them, live in the … menu on the tab strip.
/// </summary>
sealed partial class OutputPanel(
    ILogger log,
    OutputPanelSettings settings,
    OutputLogBridge bridge,
    SettingsService settingsService,
    IFocusTracker focusTracker,
    StudioLocalization localization,
    OutputRevealService? requests = null) : IPanel
{
    const string scrollId = "gaya.turian.output/log";
    const string detailNodeId = "gaya.turian.output/detail";
    const string headerId = "gaya.turian.output/header";
    const string splitId = "gaya.turian.output/split";
    const string filterId = "gaya.turian.output/filter";
    const string clearId = "gaya.turian.output/clear";
    const string toggleId = "gaya.turian.output/toggle";
    const string rowPrefix = "gaya.turian.output/row/";
    const float rowHeight = 16f;
    const float defaultSplit = 0.68f;

    string filter = "";
    bool collapse;
    bool showErrors = true;
    bool showWarnings = true;
    bool showLog = log.IsEnabled(LogLevel.Debug);
    bool showInformation = true;

    readonly LogSelection selection = new();
    int selectedIndex = -1;
    bool followTail = true;
    float priorTailBottom;
    float split = defaultSplit;
    long lastGeneration;

    Font? monoFont;
    float monoSize;

    IReadOnlyList<LogRow> rows = [];
    IReadOnlyList<LogLine> lines = [];
    LogCounts counts;
    LogLine? revealing;
    bool scrollToSelected;

    static ThemeTokens Theme => ThemeTokens.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        if (bridge.Generation != lastGeneration)
        {
            lastGeneration = bridge.Generation;
            ClearSelection();
            followTail = true;
        }

        if (gui.Pass == Pass.Pass1Build)
        {
            if (requests?.Take() is { } wanted)
            {
                PrepareReveal(wanted);
            }

            lines = LogBuffer.Snapshot();
            counts = LogView.Count(lines, settings.ShowStudio);
        }

        Navigate(gui);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter())
        {
            Header(gui);

            using (gui.Node(-1, -1, splitId).Expand().Direction(Axis.Vertical).Enter())
            {
                Rect above;
                using (gui.Node().ExpandWidth().ExpandHeight(split).Enter())
                {
                    above = gui.CurrentNode.Rect;
                    Entries(gui);
                }

                gui.Splitter(ref split, Axis.Vertical, thickness: Theme.Scale(Theme.Gap), min: 0.25f);

                Rect below;
                using (gui.Node().ExpandWidth().ExpandHeight(1f - split).Enter())
                {
                    below = gui.CurrentNode.Rect;
                    Detail(gui);
                }

                DrawDivider(gui, above, below);
            }
        }
        gui.CascadeMenu(ref menuOpen, menuAt, menu => BuildContextMenu(menu, gui));
    }

    /// <summary>
    /// Paints the strip between the list and the detail as a draggable divider: a window-colored track with a
    /// centered grip, the same thickness as the dividers between docked panels.
    /// </summary>
    static void DrawDivider(Gui gui, Rect above, Rect below)
    {
        if (gui.Pass != Pass.Pass2Render)
        {
            return;
        }

        var top = above.Y + above.H;
        var track = new Rect(above.X, top, above.W, below.Y - top);
        if (track.H <= 0f)
        {
            return;
        }

        gui.DrawRect(track, Theme.Background);
        var grip = new Vector2(Theme.Scale(28f), Theme.Scale(2f));
        gui.DrawRect(new Rect(track.X + (track.W - grip.X) / 2f, track.Y + (track.H - grip.Y) / 2f, grip.X, grip.Y),
            Theme.InkFaint);
    }

    void ClearSelection()
    {
        selection.Clear();
        selectedIndex = -1;
    }

    /// <summary>
    /// The Collapse toggle, the filter box, then one icon toggle per severity with its entry count, and the Clear
    /// button that empties the log.
    /// </summary>
    void Header(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);
        var box = Theme.Scale(13f);

        using (gui.Node(-1, height, headerId).ExpandWidth()
                   .Direction(Axis.Horizontal).Gap(10f).ContentAlignY(0.5f).Enter())
        {
            gui.Checkbox(ref collapse, localization.T("Collapse"), size: box, fontSize: Theme.Text(11f), spacing: 4f);

            filter = gui.TextInput(filter, width: 0, height: height, placeholder: localization.T("Filter output"),
                fontSize: Theme.Text(12), padding: 5, id: filterId);

            SeverityToggle(gui, "errors", Icons.LogError, counts.Errors, localization.T("Errors"), ref showErrors);
            SeverityToggle(gui, "warnings", Icons.LogWarning, counts.Warnings, localization.T("Warnings"),
                ref showWarnings);
            SeverityToggle(gui, "information", Icons.LogInfo, counts.Information, localization.T("Information"),
                ref showInformation);
            SeverityToggle(gui, "debug", Icons.LogDebug, counts.Debug, localization.T("Debug"), ref showLog);

            if (StudioControls.SmallTextButton(gui, localization.T("Clear"), clearId, Theme.Scale(62f)))
            {
                LogBuffer.Clear();
                ClearSelection();
                followTail = true;
            }
        }
    }

    void Entries(Gui gui)
    {
        using (gui.Node(-1, -1, scrollId).Expand().Enter())
        {
            gui.ScrollY();

            if (gui.Pass == Pass.Pass1Build)
            {
                rows = LogView.Project(lines, filter, showErrors, showWarnings, showLog, collapse,
                    information: showInformation, studio: settings.ShowStudio);
                if (selectedIndex >= rows.Count || selectedIndex >= 0 && !selection.Contains(rows[selectedIndex]))
                {
                    selectedIndex = selection.LastIndex(rows);
                }

                SelectRevealed();
            }

            for (var index = 0; index < rows.Count; index++)
            {
                Row(gui, rows[index], index);
            }

            if (gui.Pass == Pass.Pass2Render)
            {
                FollowTail(gui);
                if (scrollToSelected && selectedIndex >= 0)
                {
                    RevealRow(gui, selectedIndex);
                }

                scrollToSelected = false;
            }
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
        if (gui.Pass != Pass.Pass2Render || rows.Count == 0)
        {
            return;
        }

        if (focusTracker.ActivePanelId != GayaPlugin.OutputPanelId)
        {
            return;
        }

        if (gui.Focus.IsTextInputFocused)
        {
            return;
        }

        if (HandleClipboardKeys(gui))
        {
            return;
        }

        var page = Math.Max(1, PageSize(gui));
        if (gui.Input.IsKeyPressed(KeyboardKey.Up))
        {
            MoveSelection(-1, gui);
        }
        else if (gui.Input.IsKeyPressed(KeyboardKey.Down))
        {
            MoveSelection(1, gui);
        }
        else if (gui.Input.IsKeyPressed(KeyboardKey.PageUp))
        {
            MoveSelection(-page, gui);
        }
        else if (gui.Input.IsKeyPressed(KeyboardKey.PageDown))
        {
            MoveSelection(page, gui);
        }
        else if (gui.Input.IsKeyPressed(KeyboardKey.Home))
        {
            MoveSelection(int.MinValue, gui);
        }
        else if (gui.Input.IsKeyPressed(KeyboardKey.End))
        {
            MoveSelection(int.MaxValue, gui);
        }
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
        if (target == selectedIndex)
        {
            return;
        }

        if (Shift(gui))
        {
            selection.Extend(rows, target, additive: false);
        }
        else
        {
            selection.Set(rows, target);
        }

        selectedIndex = target;
        followTail = selectedIndex == rows.Count - 1;
        RevealRow(gui, selectedIndex);
    }

    /// <summary>Scrolls the selected row into view, keeping a little breathing room at each edge.</summary>
    void RevealRow(Gui gui, int index)
    {
        var scroll = gui.GetScrollState(scrollId);
        if (scroll is null)
        {
            return;
        }

        var top = 0f;
        for (var i = 0; i < index; i++)
        {
            top += RowHeight(rows[i]);
        }

        var bottom = top + RowHeight(rows[index]);
        var offset = scroll.ScrollOffset.Y;

        if (top < offset)
        {
            offset = MathF.Max(0f, top - 4f);
        }
        else if (bottom > offset + scroll.ViewportSize.Y)
        {
            offset = bottom - scroll.ViewportSize.Y + 4f;
        }
        else
        {
            return;
        }

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
        if (scroll is null)
        {
            return 1;
        }

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

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hovered = interactable.OnHover();
            if (selection.Contains(row))
            {
                gui.DrawBackgroundRect(Theme.AccentFill, 2f);
            }
            else if (hovered)
            {
                gui.DrawBackgroundRect(Theme.Hover, 2f);
            }

            _ = gui.ThemedIcon(LevelIcon(row.Line.Level), Theme.Scale(size + 3f));

            if (settings.ShowTimestamp)
            {
                gui.DrawText(
                    row.Line.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    size, Theme.InkFaint, font);
            }

            gui.DrawText(text, size, TextColor(row.Line.Level), font);

            if (row.Count > 1)
            {
                using (gui.Node().Expand().Enter()) { }
                CountBadge(gui, row.Count, height);
            }

            if (gui.Pass == Pass.Pass2Render && hovered)
            {
                HandleRowInput(gui, interactable, row, index);
            }
        }
    }

    /// <summary>The number of duplicates a collapsed row stands for, in a small badge over the right edge.</summary>
    static void CountBadge(Gui gui, int count, float height)
    {
        using (gui.Node(Theme.Scale(22f), Math.Max(Theme.Scale(13f), height - Theme.Scale(3f)))
                   .ContentAlignX(0.5f).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(Theme.Chrome, 3f);
            }

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
        if (state is null)
        {
            return;
        }

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

        if (followTail)
        {
            gui.ScrollToBottom(scrollId);
        }

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

    /// <summary>The icon that marks an entry's severity in the list.</summary>
    internal static string LevelIcon(LogLevel level) => level switch
    {
        LogLevel.Critical or LogLevel.Error => Icons.LogError,
        LogLevel.Warning => Icons.LogWarning,
        LogLevel.Debug or LogLevel.Trace => Icons.LogDebug,
        _ => Icons.LogInfo,
    };

    static GuiColor TextColor(LogLevel level) =>
        level is LogLevel.Debug or LogLevel.Trace ? Theme.InkDim : Theme.Ink;
}
