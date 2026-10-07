namespace Gaya.Plugin.Turian;

/// <summary>Edits node tag memberships with searchable choices and removable chips.</summary>
sealed class TagDrawer(LayerFilter layers, UndoService? undo = null) : IPropertyDrawer
{
    /// <summary>Whether the field represents a node's tag collection.</summary>
    public static bool Handles(FormField field) => field.Target is Node && field.Name == nameof(Node.Tags);

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context),
            context.IsModified?.Invoke(field) == true);

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        var owners = field.Sources.Select(ReadTags).ToArray();
        var selected = owners[0];
        var mixed = owners.Skip(1).Any(tags => !selected.ToHashSet(StringComparer.Ordinal).SetEquals(tags));
        var options = layers.Settings.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal).ToArray();
        var theme = ThemeTokens.Current;
        var result = gui.MultiDropdown(options, selected, comparer: StringComparer.Ordinal, chips: !mixed,
            mixed: mixed, isMixed: tag => owners.Any(tags => tags.Contains(tag) != selected.Contains(tag)),
            width: 0, height: theme.Scale(theme.RowHeight), fontSize: theme.Text(12f),
            placeholder: "none", enabled: !field.IsReadOnly, filePath: $"{id}/tags");
        if (result.Changed)
            InspectorSelectionEdits.ApplyTags(field,
                result.Changes.Select(change => (change.Item, change.Selected)).ToArray(), undo);
        return true;
    }

    static string[] ReadTags(FormField field) => field.GetValue() is IEnumerable<string> tags ? [.. tags] : [];
}
