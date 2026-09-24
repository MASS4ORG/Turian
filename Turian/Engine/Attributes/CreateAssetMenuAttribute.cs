namespace Turian;

/// <summary>
/// Marks a data-asset type as creatable from editor menus.
/// The provided path is used to build nested menu entries, using '/' as the separator.
/// </summary>
/// <param name="path">The menu path shown in the editor.</param>
/// <param name="fileName">The default file name suggested when creating the asset.</param>
[SuppressPrivate]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CreateAssetMenuAttribute(string path, string fileName = "New Data Asset")
    : Attribute
{
    /// <summary>
    /// Gets the menu path shown in the editor.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// Gets the default file name suggested when creating the asset.
    /// </summary>
    public string FileName { get; } = fileName;
}
