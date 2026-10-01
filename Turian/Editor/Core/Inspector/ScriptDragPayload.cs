namespace Turian.Editor.Core;

/// <summary>Payload carried by a user-code source file dragged from the asset browser.</summary>
/// <param name="ComponentType">The component the file defines.</param>
/// <param name="Name">Display name, for the drag ghost.</param>
/// <param name="AssetPath">The dragged file, for drops that copy it.</param>
public readonly record struct ScriptDragPayload(Type ComponentType, string Name, string? AssetPath = null);
