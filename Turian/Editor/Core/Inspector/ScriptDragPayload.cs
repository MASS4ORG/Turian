namespace Turian.Editor.Core;

/// <summary>Payload carried by a user-code source file dragged from the asset browser.</summary>
public readonly record struct ScriptDragPayload(Type ComponentType, string Name);
