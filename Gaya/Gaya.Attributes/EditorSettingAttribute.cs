namespace Gaya;

/// <summary>Declares a settings page on a class, or the label and description of one option on a member.</summary>
/// <param name="path">The page's category path, or a member's display label.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false, Inherited = false)]
public sealed class EditorSettingAttribute(string path = "") : Attribute
{
    /// <summary>The category path on a class, or the display label on a member.</summary>
    public string Path { get; } = path;

    /// <summary>The persisted page id; an empty value derives it from the declaring type.</summary>
    public string Id { get; set; } = "";

    /// <summary>A description shown below the page or option title.</summary>
    public string Description { get; set; } = "";

    /// <summary>The page's sort order among sibling pages.</summary>
    public int Order { get; set; }

    /// <summary>Whether the page's values are stored with the project instead of the user.</summary>
    public bool Workspace { get; set; }

    /// <summary>Whether the page is persisted without appearing in the Settings panel.</summary>
    public bool Hidden { get; set; }

    /// <summary>Resolves the storage id for the annotated settings type.</summary>
    public string IdFor(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return string.IsNullOrWhiteSpace(Id) ? $"usercode.settings.{type.FullName}" : Id.Trim();
    }
}
