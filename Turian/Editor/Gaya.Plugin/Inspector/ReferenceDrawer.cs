namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws <c>AssetReference</c>, <c>NodeRef</c> and <c>ComponentRef</c> fields as a reference slot:
/// drop a row from the asset browser or scene tree on it, click it to select what it points at, or
/// use its pick button for a searchable list of the values its declared type allows.
/// </summary>
/// <param name="picker">Supplies and validates the candidates for a field.</param>
/// <param name="inspector">Receives a revealed node, which is what selects it in the scene tree.</param>
/// <param name="reveal">Asks the asset browser to show a revealed asset.</param>
sealed class ReferenceDrawer(ReferencePicker picker, NodeInspectorController inspector,
    AssetRevealService reveal)
{
    const float pickerWidth = 280f;
    const float pickerHeight = 240f;

    static StudioTheme Theme => StudioTheme.Current;

    static float RowHeight => Theme.Scale(18f);

    string? openFieldId;
    ReferenceField? openField;
    string search = string.Empty;
    Rect openAnchor;

    /// <summary>
    /// Draws <paramref name="field"/> when it holds a reference.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="field">The member to edit.</param>
    /// <param name="id">A unique id for this row's controls.</param>
    /// <returns>False when the field is not a reference, so the caller falls back to its own drawers.</returns>
    public bool TryDraw(Gui gui, FormField field, string id)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (ReferenceField.TryCreate(field) is not { } reference) return false;

        var result = gui.ObjectField(
            picker.DisplayName(reference), $"{id}/ref",
            accept: payload => payload is ReferenceDragPayload drop
                               && !reference.IsReadOnly
                               && picker.Accepts(reference, drop.Id),
            isEmpty: reference.IsEmpty,
            showClear: !reference.IsReadOnly);

        switch (result.Action)
        {
            case ObjectFieldAction.Drop when result.Payload is ReferenceDragPayload drop:
                reference.Set(drop.Id);
                break;
            case ObjectFieldAction.Clear:
                reference.Clear();
                break;
            case ObjectFieldAction.Pick when !reference.IsReadOnly:
                var reopening = openFieldId == id;
                openFieldId = reopening ? null : id;
                openField = reopening ? null : reference;
                search = string.Empty;
                break;
            case ObjectFieldAction.Reveal:
                Reveal(reference);
                break;
        }

        if (openFieldId == id)
        {
            openField = reference;
            if (gui.Pass == Pass.Pass2Render) openAnchor = gui.CurrentNode.Rect;
        }

        return true;
    }

    /// <summary>
    /// Selects what the reference points at where it lives: an asset in the browser, a node or a
    /// component's owner in the scene tree. Unity's "ping", and the reason clicking a slot is not
    /// what opens the picker.
    /// </summary>
    void Reveal(ReferenceField reference)
    {
        if (reference.IsEmpty) return;

        if (reference.Kind == ReferenceKind.Asset)
        {
            reveal.Reveal(reference.CurrentId);
            return;
        }

        if (picker.FindNode(reference.CurrentId) is { } node) inspector.Select(node);
    }

    /// <summary>
    /// Draws the open picker, if any. Call it at the top level of the panel rather than inside a
    /// field row: a popup opened from inside the inspector's scrolled subtree is clipped by it.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    public void DrawPendingPicker(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        if (openFieldId is null || openField is not { } reference) return;

        var open = true;
        gui.Popup(ref open, () => PickerContent(gui, reference), pickerWidth, pickerHeight,
            $"Select {reference.TargetType.Name}",
            PopupAnchor.Below(gui, openAnchor, pickerWidth, pickerHeight),
            backgroundColor: Theme.Panel, borderColor: Theme.Border, titleBarColor: Theme.Border, titleTextColor: Theme.Ink);

        if (open) return;

        openFieldId = null;
        openField = null;
    }

    void PickerContent(Gui gui, ReferenceField reference)
    {
        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4).Padding(6).Enter())
        {
            search = gui.TextInput(search, width: pickerWidth - 24, height: RowHeight, placeholder: "Search…");

            using (gui.Node().Expand().Direction(Axis.Vertical).Enter())
            {
                gui.ScrollY();

                if (CandidateRow(gui, $"None ({reference.TargetType.Name})", string.Empty, "none"))
                {
                    reference.Clear();
                    openFieldId = null;
                    openField = null;
                }

                foreach (var candidate in picker.Candidates(reference, search))
                {
                    if (!CandidateRow(gui, candidate.Name, candidate.Detail, candidate.Id.ToString())) continue;

                    reference.Set(candidate.Id);
                    openFieldId = null;
                    openField = null;
                }
            }
        }
    }

    static bool CandidateRow(Gui gui, string name, string detail, string id)
    {
        using (gui.Node(-1, RowHeight, $"candidate/{id}")
                   .ExpandWidth().Direction(Axis.Horizontal).Gap(6).Padding(4, 0).ContentAlignY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            if (interactable.OnHover()) gui.DrawBackgroundRect(Theme.AccentFill, 2);

            gui.DrawText(name, Theme.Text(12), Theme.Ink, centerInRect: false);
            if (detail.Length > 0) gui.DrawText(detail, Theme.Text(10), Theme.InkDim, centerInRect: false);

            return interactable.OnClick();
        }
    }
}
