namespace Turian.Editor.Core;

/// <summary>
/// A button the inspector draws to run an action on the inspected object — the editor half of a
/// <c>[Button]</c> method, the way Odin lets a tool run straight from a scriptable object's members.
/// </summary>
/// <param name="Label">The text the button shows.</param>
/// <param name="Invoke">Runs the annotated method on the form's target.</param>
public sealed record InspectorButton(string Label, Action Invoke);
