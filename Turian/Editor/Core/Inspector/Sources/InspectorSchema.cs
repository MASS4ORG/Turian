namespace Turian.Editor.Core;

/// <summary>What the Inspector shows for one selected object.</summary>
/// <param name="Title">The heading above the properties.</param>
/// <param name="Properties">The editable values, in the order they are shown within their category.</param>
/// <param name="Actions">Buttons under the heading's properties; shown for a single selection.</param>
public sealed record InspectorSchema(
    string Title,
    IReadOnlyList<InspectorProperty> Properties,
    IReadOnlyList<Button>? Actions = null);

/// <summary>
/// One value of a selected object. The Inspector reads and writes it only through these accessors and records no undo
/// step, so <paramref name="Set"/> has to record the change itself, such as in its document's history. A value that is
/// itself a collection or an object with members is edited in place.
/// </summary>
/// <param name="Id">Names the property; selected objects share the properties whose id and type match.</param>
/// <param name="Label">The name shown beside the value.</param>
/// <param name="ValueType">The type of the value, which selects its editor.</param>
/// <param name="Get">Reads the value; called every frame, so it should be cheap.</param>
/// <param name="Set">Writes a value that differs from the current one.</param>
public sealed record InspectorProperty(
    string Id,
    string Label,
    Type ValueType,
    Func<object?> Get,
    Action<object?> Set)
{
    /// <summary>The section the property is grouped under; empty keeps it under the heading.</summary>
    public string Category { get; init; } = "";

    /// <summary>A hint naming the editor wanted, for field drawers that recognise it.</summary>
    public string? Editor { get; init; }

    /// <summary>Explains the property when its row is hovered.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Whether the value is only shown.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>The value in effect while the property is not set, for field drawers that show it.</summary>
    public object? Default { get; init; }

    /// <summary>Whether the object carries a value of its own, which marks the row as modified.</summary>
    public Func<bool>? IsSet { get; init; }

    /// <summary>Removes the object's own value; offered from the row's context menu.</summary>
    public Action? Reset { get; init; }

    /// <summary>Constraints and presentation hints, for field drawers that honour them.</summary>
    public IReadOnlyList<Attribute> Attributes { get; init; } = [];
}
