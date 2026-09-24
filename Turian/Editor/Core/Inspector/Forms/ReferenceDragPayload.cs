namespace Turian.Editor.Core;

/// <summary>
/// What a draggable asset or scene row carries. The id alone is enough: a reference field validates
/// it against the registry its own kind names, so a node dropped on an asset field is rejected
/// without the payload having to say which it is.
/// </summary>
/// <param name="Id">The asset or node id.</param>
/// <param name="Name">Display name, for the drag ghost and for logging.</param>
public readonly record struct ReferenceDragPayload(Guid Id, string Name);
