using System.Collections.ObjectModel;

namespace Turian.Editor.Core;

/// <summary>Applies membership edits to each inspected owner's own value in one undo transaction.</summary>
public static class InspectorSelectionEdits
{
    /// <summary>Changes selected tags while retaining each owner's other and unrecognized tags.</summary>
    public static void ApplyTags(FormField field, IReadOnlyList<(string Tag, bool Selected)> changes,
        UndoService? undo = null) => Apply(field, value => EditTags(value, changes), "Edit Tags", undo);

    /// <summary>Changes selected layer slots while retaining each owner's other mask bits.</summary>
    public static void ApplyLayerMask(FormField field, IReadOnlyList<(int Index, bool Selected)> changes,
        UndoService? undo = null) => Apply(field, value => EditMask(value, changes), "Edit Layer Mask", undo);

    static void Apply(FormField field, Func<object?, object?> edit, string label, UndoService? undo)
    {
        if (field.IsReadOnly) return;
        undo?.BeginGesture();
        try
        {
            RecordOwners(field, label, undo);
            WriteOwners(field, edit);
        }
        finally { undo?.EndGesture(); }
    }

    static void RecordOwners(FormField field, string label, UndoService? undo)
    {
        if (undo is null) return;
        foreach (var owner in field.Sources.Select(source => source.Target).OfType<IdObject>().Distinct())
            undo.RecordObject(owner, label);
    }

    static void WriteOwners(FormField field, Func<object?, object?> edit)
    {
        foreach (var source in field.Sources)
        {
            var value = source.GetValue();
            var edited = edit(value);
            if (!ReferenceEquals(value, edited) && !Equals(value, edited)) source.SetValue(edited);
        }
    }

    static object? EditTags(object? value, IReadOnlyList<(string Tag, bool Selected)> changes)
    {
        var original = value is IEnumerable<string> tags ? tags.ToArray() : [];
        var edited = original.ToList();
        foreach (var (tag, selected) in changes)
        {
            if (!selected) edited.RemoveAll(item => string.Equals(item, tag, StringComparison.Ordinal));
            else if (!edited.Contains(tag, StringComparer.Ordinal)) edited.Add(tag);
        }
        return original.SequenceEqual(edited) ? value : new ObservableCollection<string>(edited);
    }

    static LayerMask EditMask(object? value, IReadOnlyList<(int Index, bool Selected)> changes)
    {
        var mask = (LayerMask)(value ?? LayerMask.Nothing);
        foreach (var (index, selected) in changes)
        {
            var bit = LayerMask.FromLayer(index);
            mask = selected ? mask | bit : mask & ~bit;
        }
        return mask;
    }
}
