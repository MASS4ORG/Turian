namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws one value type's <see cref="FormField"/> the way it wants to be drawn, registered by
/// decorating the class with <see cref="CustomEditorAttribute"/>. An editor owns its whole
/// appearance — a vector is one labelled row, a transform is three — so it replaces the dispatcher's
/// per-type switch rather than slotting into it.
/// </summary>
interface IValueEditor
{
    /// <summary>Draws the whole labelled field. An editor may span several rows (a transform).</summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="field">The member to edit.</param>
    /// <param name="id">A unique id for this editor's controls.</param>
    void Draw(Gui gui, FormField field, string id);

    /// <summary>
    /// Draws only the control, for a caller that lays out the label itself — the settings panel
    /// places a title and its description above the editor rather than beside it.
    /// </summary>
    /// <param name="gui">The GUI for this frame.</param>
    /// <param name="field">The member to edit.</param>
    /// <param name="id">A unique id for this editor's controls.</param>
    /// <returns>False when the editor has no value-only form, so the caller can fall back.</returns>
    bool DrawValue(Gui gui, FormField field, string id);
}
