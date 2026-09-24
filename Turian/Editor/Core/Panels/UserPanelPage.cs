namespace Turian.Editor.Core;

/// <summary>
/// One dockable panel discovered in user code: the instance it shows, and the menu path that opens it.
/// </summary>
/// <param name="Id">Stable id derived from the declaring type, so a recompile replaces rather than duplicates.</param>
/// <param name="Name">The panel's title, and the label of the menu item that opens it.</param>
/// <param name="Path">'/'-separated menu path the attribute declared, e.g. <c>Tools/My Panel</c>.</param>
/// <param name="Target">The panel object, created once per scan.</param>
public sealed record UserPanelPage(string Id, string Name, string Path, object Target);
