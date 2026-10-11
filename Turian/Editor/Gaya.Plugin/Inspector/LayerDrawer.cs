namespace Gaya.Plugin.Turian;

/// <summary>Edits layer indices and masks using the project's named slots.</summary>
sealed class LayerDrawer(LayerFilter layers, UndoService? undo = null) : IPropertyDrawer
{
    readonly Dictionary<string, int> frameSelections = new(StringComparer.Ordinal);

    /// <summary>Whether this field is a layer mask or one of a node's two layer indices.</summary>
    public static bool Handles(FormField field) => field.ValueType == typeof(LayerMask)
        || field.Target is Node && field.Name is nameof(Node.PhysicsLayer) or nameof(Node.RenderLayer);

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context),
            context.IsModified?.Invoke(field) == true);

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var slots = (field.Name == nameof(Node.PhysicsLayer)
            ? layers.Settings.PhysicsLayers : layers.Settings.RenderLayers)
            .Where(slot => (uint)slot.Index < 32).OrderBy(slot => slot.Index).ToArray();
        if (field.ValueType == typeof(LayerMask)) DrawMask(gui, field, slots, id);
        else DrawLayer(gui, field, slots, id);
        return true;
    }

    void DrawLayer(Gui gui, FormField field, LayerSlot[] slots, string id)
    {
        var value = (int)(field.GetValue() ?? 0);
        var current = field.HasMixedValue ? -1 : Array.FindIndex(slots, slot => slot.Index == value);
        var next = Dropdown(gui, [.. slots.Select(slot => $"{slot.Index}: {slot.Name}")], current, id, !field.IsReadOnly);
        if (gui.Pass == Pass.Pass2Render && next >= 0 && next != current) field.SetValue(slots[next].Index);
    }

    void DrawMask(Gui gui, FormField field, LayerSlot[] slots, string id)
    {
        var masks = field.Sources.Select(source => (LayerMask)(source.GetValue() ?? LayerMask.Nothing)).ToArray();
        var options = slots.Select(slot => slot.Index).ToArray();
        var selected = options.Where(masks[0].Contains).ToArray();
        var labels = slots.ToDictionary(slot => slot.Index, slot => $"{slot.Index}: {slot.Name}");
        var theme = ThemeTokens.Current;
        var result = gui.MultiDropdown(options, selected, display: index => labels[index],
            mixed: field.HasMixedValue,
            isMixed: index => masks.Any(mask => mask.Contains(index) != masks[0].Contains(index)),
            width: 0, height: theme.Scale(theme.RowHeight), fontSize: theme.Text(12f),
            placeholder: "none", enabled: !field.IsReadOnly, filePath: $"{id}/layer-mask");
        if (result.Changed)
            InspectorSelectionEdits.ApplyLayerMask(field,
                result.Changes.Select(change => (change.Item, change.Selected)).ToArray(), undo);
    }

    int Dropdown(Gui gui, string[] labels, int current, string id, bool enabled)
    {
        var theme = ThemeTokens.Current;
        if (gui.Pass == Pass.Pass2Render && frameSelections.TryGetValue(id, out var selection)) current = selection;
        var next = gui.Dropdown(labels, current, width: 0, height: theme.Scale(theme.RowHeight), fontSize: theme.Text(12f),
            placeholder: "—", enabled: enabled, filePath: $"{id}/layer");
        if (gui.Pass == Pass.Pass1Build) frameSelections[id] = next;
        else frameSelections.Remove(id);
        return next;
    }
}
