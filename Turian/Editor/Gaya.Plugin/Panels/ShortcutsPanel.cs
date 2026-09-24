namespace Gaya.Plugin.Turian;

/// <summary>
/// The keybindings editor: every command that has a shortcut, grouped by the context it fires in,
/// with a filter box, in-place rebinding and a reset. Clicking a row's chord listens for the next key
/// sequence; a second stroke pressed within the arming window makes it a chord.
/// </summary>
/// <remarks>
/// Rebinding never refuses a sequence. A clash is reported on the row and in the footer instead, the
/// way an IDE does it, because the user often means to take a shortcut away from something else.
/// </remarks>
sealed class ShortcutsPanel(IShortcutService shortcuts, ICommandCatalog commands,
    IPanelAccessor panels) : IPanel
{
    const float chordWidth = 150f;
    const float rowHeight = 24f;

    string filter = "";
    string? capturing;
    KeyStroke first = KeyStroke.None;

    static StudioTheme Theme => StudioTheme.Current;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(Theme.Gap).Padding(10f, 8f).Enter())
        {
            Header(gui);

            using (gui.Node().Expand().Direction(Axis.Vertical).Gap(2f).Enter())
            {
                gui.ScrollY();
                Rows(gui);
            }

            Footer(gui);
        }
    }

    void Header(Gui gui)
    {
        var height = Theme.Scale(Theme.RowHeight + 4f);

        using (gui.Node(-1, height, "shortcuts/header").ExpandWidth().Direction(Axis.Horizontal)
                   .Gap(Theme.Gap).Enter())
        {
            using (gui.Node(-1, height, "shortcuts/header/filter").Expand().Enter())
                filter = gui.TextInput(filter, width: 0, height: height, placeholder: "Search commands",
                    fontSize: Theme.Text(12), padding: 5, id: "shortcuts/filter");

            if (StudioControls.SmallTextButton(gui, "Reset All", "shortcuts/resetAll", Theme.Scale(80f)))
            {
                shortcuts.ResetAll();
                Cancel();
            }
        }
    }

    /// <summary>One group per context, global first, each headed by the panel the shortcuts belong to.</summary>
    void Rows(Gui gui)
    {
        var visible = shortcuts.Entries
            .Where(entry => Matches(entry, Title(entry.CommandId)))
            .GroupBy(entry => entry.Context, StringComparer.Ordinal)
            .OrderBy(group => ShortcutContexts.IsGlobal(group.Key) ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in visible)
        {
            GroupHeader(gui, ShortcutContexts.IsGlobal(group.Key) ? "Global" : panels.Title(group.Key));

            foreach (var entry in group)
                Row(gui, entry);
        }
    }

    static void GroupHeader(Gui gui, string label)
    {
        using (gui.Node(-1, Theme.Scale(rowHeight), $"shortcuts/group/{label}").ExpandWidth()
                   .ContentAlignY(0.5f).Margin(0f, Theme.Scale(6f), 0f, 0f).Enter())
            gui.DrawText(label.ToUpperInvariant(), Theme.Text(10f), Theme.InkDim, centerInRect: false);
    }

    void Row(Gui gui, ShortcutEntry entry)
    {
        var id = $"shortcuts/row/{entry.CommandId}";
        var height = Theme.Scale(rowHeight);
        var clashes = shortcuts.Conflicts.Any(conflict => conflict.CommandIds.Contains(entry.CommandId));

        using (gui.Node(-1, height, id).ExpandWidth().Direction(Axis.Horizontal).Gap(6f)
                   .ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnHover())
                gui.DrawBackgroundRect(Theme.Hover, 3f);

            using (gui.Node(-1, height, $"{id}/label").Expand().ContentAlignY(0.5f).Enter())
                gui.DrawText(Title(entry.CommandId), Theme.Text(12f),
                    clashes ? Theme.Error : Theme.Ink, centerInRect: false);

            Chord(gui, entry, $"{id}/chord", height);

            if (entry.IsModified && StudioControls.SmallTextButton(gui, "Reset", $"{id}/reset", Theme.Scale(52f)))
            {
                shortcuts.Reset(entry.CommandId);
                Cancel();
            }
        }
    }

    /// <summary>
    /// The chord cell. While it is the capturing row it reads the keyboard directly: Escape cancels,
    /// Backspace clears the binding, and anything else is taken as the sequence.
    /// </summary>
    void Chord(Gui gui, ShortcutEntry entry, string id, float height)
    {
        var listening = capturing == entry.CommandId;

        using (gui.Node(Theme.Scale(chordWidth), height, id).BlockInput().ContentAlignY(0.5f)
                   .ContentAlignX(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(listening ? Theme.Accent : hot ? Theme.Border : Theme.Chrome, 3f);

                if (hot && interactable.OnClick()) Begin(entry.CommandId);
                if (listening) Capture(gui, entry.CommandId);
            }

            gui.DrawText(Label(entry, listening), Theme.Text(12f),
                listening ? Theme.Background : Theme.Ink);
        }
    }

    string Label(ShortcutEntry entry, bool listening)
    {
        if (!listening) return entry.Display.Length > 0 ? entry.Display : "—";
        return first.IsNone ? "Press a key…" : $"{first}, …";
    }

    void Capture(Gui gui, string commandId)
    {
        if (gui.Input.IsKeyPressed(KeyboardKey.Escape))
        {
            Cancel();
            return;
        }

        if (gui.Input.IsKeyPressed(KeyboardKey.Backspace) && first.IsNone)
        {
            shortcuts.Rebind(commandId, KeyStroke.None);
            Cancel();
            return;
        }

        if (KeyStroke.Read(gui.Input) is not { } stroke) return;

        // A stroke that is only modifiers arms nothing; a second stroke completes the chord the first
        // one started, which is how Ctrl+K Ctrl+S is entered.
        if (first.IsNone && stroke.Modifiers != KeyModifiers.None && shortcuts.Entries
                .Any(entry => entry.Effective is { Second: not null } binding && binding.Stroke == stroke))
        {
            first = stroke;
            return;
        }

        if (first.IsNone) shortcuts.Rebind(commandId, stroke);
        else shortcuts.Rebind(commandId, first, stroke);

        Cancel();
    }

    /// <summary>The clashes the whole list has, so the user sees a rebinding's cost without hunting.</summary>
    void Footer(Gui gui)
    {
        var conflicts = shortcuts.Conflicts;
        if (conflicts.Count == 0) return;

        using (gui.Node(-1, Theme.Scale(rowHeight), "shortcuts/conflicts").ExpandWidth()
                   .ContentAlignY(0.5f).Enter())
            gui.DrawText(
                $"{conflicts.Count} conflict(s): {string.Join(", ", conflicts.Take(3).Select(Describe))}",
                Theme.Text(11f), Theme.Error, centerInRect: false);
    }

    string Describe(ShortcutConflict conflict) =>
        $"{conflict.Display} → {string.Join(" / ", conflict.CommandIds.Select(Title))}";

    void Begin(string commandId)
    {
        capturing = commandId;
        first = KeyStroke.None;
        shortcuts.IsCapturing = true;
    }

    void Cancel()
    {
        capturing = null;
        first = KeyStroke.None;
        shortcuts.IsCapturing = false;
    }

    string Title(string commandId) => commands.Find(commandId)?.Title ?? commandId;

    bool Matches(ShortcutEntry entry, string title) =>
        filter.Length == 0
        || title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || entry.Display.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
