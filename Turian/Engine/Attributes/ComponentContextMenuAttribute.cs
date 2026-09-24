namespace Turian;

/// <summary>
/// Marks a component type as available in editor add-component menus.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentContextMenuAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComponentContextMenuAttribute"/> class.
    /// </summary>
    /// <param name="path">The menu path shown by the editor.</param>
    public ComponentContextMenuAttribute(string? path = null)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the menu path shown by the editor.
    /// </summary>
    public string? Path { get; }
}
