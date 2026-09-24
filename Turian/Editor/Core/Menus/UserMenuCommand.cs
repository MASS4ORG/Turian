namespace Turian.Editor.Core;

/// <summary>
/// One menu entry contributed by user code through <see cref="MenuItemAttribute"/>.
/// </summary>
/// <param name="Path">
/// The menu path the attribute declared, such as <c>Tools/Rebuild Lighting</c>. The first segment is
/// the top-level menu, the last is the item, and anything between is a submenu.
/// </param>
/// <param name="Id">
/// Stable id derived from the declaring method, so a shell can register the entry as a command and
/// replace it on the next recompile rather than accumulating duplicates.
/// </param>
/// <param name="Invoke">Runs the method, resolving a single parameter from the provider when it has one.</param>
public sealed record UserMenuCommand(string Path, string Id, Action<IServiceProvider> Invoke)
{
    /// <summary>The last path segment — what the menu item is labelled.</summary>
    public string Label => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The first path segment — which top-level menu the entry belongs under.</summary>
    public string Menu => Path.Split('/', 2)[0];

    /// <summary>
    /// The segments between the menu and the item, as a <c>/</c>-joined path, or an empty string when
    /// the entry sits directly in its top-level menu.
    /// </summary>
    public string SubmenuPath
    {
        get
        {
            var segments = Path.Split('/');
            return segments.Length <= 2 ? string.Empty : string.Join('/', segments[1..^1]);
        }
    }
}
