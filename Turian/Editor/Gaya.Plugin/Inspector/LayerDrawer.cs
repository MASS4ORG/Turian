namespace Gaya.Plugin.Turian;

/// <summary>Edits layer indices and masks using the project's named slots.</summary>
sealed class LayerDrawer(LayerFilter layers) : IPropertyDrawer
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
        var next = Dropdown(gui, [.. slots.Select(slot => $"{slot.Index}: {slot.Name}")], current, id);
        if (gui.Pass == Pass.Pass2Render && next >= 0 && next != current) field.SetValue(slots[next].Index);
    }

    void DrawMask(Gui gui, FormField field, LayerSlot[] slots, string id)
    {
        var mask = (LayerMask)(field.GetValue() ?? LayerMask.Nothing);
        string[] labels =
        [
            MaskLabel(field, mask),
            "Everything", "Nothing",
            .. slots.Select(slot => $"{(mask.Contains(slot.Index) ? "✓ " : "")} {slot.Index}: {slot.Name}"),
        ];
        var next = Dropdown(gui, labels, 0, id);
        if (gui.Pass != Pass.Pass2Render || next <= 0) return;
        if (next == 1) field.SetValue(LayerMask.Everything);
        else if (next == 2) field.SetValue(LayerMask.Nothing);
        else
        {
            ToggleBit(field, LayerMask.FromLayer(slots[next - 3].Index), mask);
        }
    }

    static string MaskLabel(FormField field, LayerMask mask) => field.HasMixedValue ? "—"
        : mask == LayerMask.Everything ? "Everything" : mask == LayerMask.Nothing ? "Nothing" : $"0x{mask.Value:X8}";

    static void ToggleBit(FormField field, LayerMask bit, LayerMask mask)
    {
        var remove = mask.Intersects(bit);
        foreach (var source in field.Sources)
        {
            var own = (LayerMask)(source.GetValue() ?? LayerMask.Nothing);
            source.SetValue(remove ? own & ~bit : own | bit);
        }
    }

    int Dropdown(Gui gui, string[] labels, int current, string id)
    {
        var theme = StudioTheme.Current;
        if (gui.Pass == Pass.Pass2Render && frameSelections.TryGetValue(id, out var selection)) current = selection;
        var next = gui.Dropdown(labels, current, width: 0, height: theme.Scale(theme.RowHeight), fontSize: theme.Text(12f),
            backgroundColor: theme.Field, borderColor: theme.Border, textColor: theme.Ink,
            dropdownColor: theme.Field, hoverColor: theme.Hover, selectedColor: theme.AccentFill,
            placeholder: "—", filePath: $"{id}/layer");
        if (gui.Pass == Pass.Pass1Build) frameSelections[id] = next;
        else frameSelections.Remove(id);
        return next;
    }
}
