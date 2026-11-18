namespace Gaya.Sdk;

/// <summary>What a custom settings editor is handed to draw one member of a page.</summary>
/// <param name="page">The page the member belongs to.</param>
/// <param name="member">The member's name.</param>
/// <param name="id">A node id unique to this member, to prefix the ids of the editor's own nodes.</param>
/// <param name="read">Reads the member's current value.</param>
/// <param name="write">Stores a new value in the member and persists it.</param>
public sealed class SettingsFieldContext(SettingsPageDescriptor page, string member, string id, Func<object?> read,
    Action<object?> write)
{
    /// <summary>The page the member belongs to.</summary>
    public SettingsPageDescriptor Page { get; } = page;

    /// <summary>The member's name.</summary>
    public string Member { get; } = member;

    /// <summary>A node id unique to this member, to prefix the ids of the editor's own nodes.</summary>
    public string Id { get; } = id;

    /// <summary>The member's current value.</summary>
    public object? Value => read();

    /// <summary>Stores a new value in the member and persists it, as an edit in the panel does.</summary>
    /// <param name="value">The value to store.</param>
    public void SetValue(object? value) => write(value);
}

/// <summary>Draws the control of one settings member in place of the generic form control for its type.</summary>
public interface ISettingsFieldEditor
{
    /// <summary>Draws the control inside the member's row; the title, scope controls and description are the panel's.</summary>
    /// <param name="gui">The GUI being drawn, in either pass.</param>
    /// <param name="field">The member being edited.</param>
    void Draw(Gui gui, SettingsFieldContext field);
}

/// <summary>The custom editors plugins registered for settings members, by page and member.</summary>
public interface ISettingsFieldEditors
{
    /// <summary>Registers the editor of a member; one already registered for it is replaced.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="editor">The editor.</param>
    void Register(string pageId, string member, ISettingsFieldEditor editor);

    /// <summary>Finds the editor registered for a member.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name.</param>
    /// <returns>The editor, or <c>null</c> when the member uses the generic control.</returns>
    ISettingsFieldEditor? Find(string pageId, string member);
}
