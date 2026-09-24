namespace Turian.Editor.Core;

/// <summary>What to do with a document's unsaved edits when it closes.</summary>
public enum UnsavedChanges
{
    /// <summary>Throw the edits away.</summary>
    Discard,

    /// <summary>Write the edits to disk first.</summary>
    Save
}
