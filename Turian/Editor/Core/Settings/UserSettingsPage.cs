namespace Turian.Editor.Core;

/// <summary>
/// One settings page discovered in user code: the instance the editor edits, and where it belongs in
/// the category tree.
/// </summary>
/// <param name="Id">Stable id derived from the declaring type, so a recompile replaces rather than duplicates.</param>
/// <param name="Path">'/'-separated place in the category tree; the last segment is the title.</param>
/// <param name="Target">The settings object, created once per scan.</param>
/// <param name="Description">A sentence shown under the page title.</param>
/// <param name="Order">Sort order among sibling pages.</param>
/// <param name="Workspace">Whether the page is stored with the project rather than with the user.</param>
public sealed record UserSettingsPage(
    string Id, string Path, object Target, string Description, int Order, bool Workspace);
