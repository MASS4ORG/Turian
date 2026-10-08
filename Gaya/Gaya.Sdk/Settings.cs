namespace Gaya.Sdk;

/// <summary>Where a settings page is stored, which is also how the panel groups its tabs.</summary>
public enum SettingsScope
{
    /// <summary>With the user, beside the editor's other preferences. Follows them across projects.</summary>
    User,

    /// <summary>With the project, so it is shared by everyone who opens it.</summary>
    Workspace,
}

/// <summary>
/// One page of editor settings: a plain object whose public members are the options. The studio
/// reflects over <see cref="Target"/> to draw the page, and persists the same members between runs.
/// </summary>
public sealed class SettingsPageDescriptor
{
    /// <summary>Reads all page configuration from the target's class-level settings attribute.</summary>
    public SettingsPageDescriptor(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var type = target.GetType();
        var attribute = type.GetCustomAttribute<EditorSettingAttribute>()
                        ?? throw new ArgumentException("Settings classes must declare [EditorSetting].", nameof(target));
        if (string.IsNullOrWhiteSpace(attribute.Path))
            throw new ArgumentException("Settings classes must declare a category path.", nameof(target));
        Target = target;
        Id = attribute.IdFor(type);
        Path = attribute.Path.Trim();
        Scope = attribute.Workspace ? SettingsScope.Workspace : SettingsScope.User;
        Order = attribute.Order;
        Description = attribute.Description;
        Hidden = attribute.Hidden;
        Defaults = SettingsDefaults.Capture(target);
    }

    /// <summary>The stable id used to store this page's values.</summary>
    public string Id { get; }

    /// <summary>The category path declared by the settings class.</summary>
    public string Path { get; }

    /// <summary>The live settings instance shared by its consumers and editors.</summary>
    public object Target { get; }

    /// <summary>Where this page's values are persisted.</summary>
    public SettingsScope Scope { get; }

    /// <summary>The declared sort order among sibling pages.</summary>
    public int Order { get; }

    /// <summary>A sentence drawn under the page title.</summary>
    public string Description { get; }

    /// <summary>
    /// Whether the settings panel leaves this page out of its category list. For a page that only
    /// exists to be persisted — the shortcut overrides, which have an editor of their own.
    /// </summary>
    public bool Hidden { get; }

    /// <summary>
    /// What each member held before anything was restored or edited — the values the class itself
    /// declares. Captured on construction, which is why a contributor hands over a freshly built
    /// object: it is what "revert to default" restores and what marks a member as modified.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Defaults { get; }

    /// <summary>The last segment of <see cref="Path"/> — what the category list and heading show.</summary>
    public string Title => Path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        is { Length: > 0 } segments
        ? segments[^1]
        : Id;
}

/// <summary>
/// Reads a settings object's declared values. Scalars only in practice: a member holding a list or
/// another object is captured by reference, so reverting it cannot restore more than that reference.
/// </summary>
public static class SettingsDefaults
{
    const BindingFlags scope = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Snapshots every readable public member of an object.</summary>
    /// <param name="target">The object to read.</param>
    /// <returns>Member name to value.</returns>
    public static IReadOnlyDictionary<string, object?> Capture(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in target.GetType().GetProperties(scope).Where(p => p is { CanRead: true, CanWrite: true }))
            values[property.Name] = Read(() => property.GetValue(target));

        foreach (var field in target.GetType().GetFields(scope).Where(f => !f.IsInitOnly))
            values[field.Name] = Read(() => field.GetValue(target));

        return values;
    }

    /// <summary>A getter on a half-built object can throw; such a member simply has no default.</summary>
    static object? Read(Func<object?> get)
    {
        try
        {
            return get();
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}

/// <summary>Collects <see cref="SettingsPageDescriptor"/> contributions.</summary>
public interface ISettingsRegistry
{
    /// <summary>
    /// Adds a page, restoring its persisted values into the target first. Re-registering an id
    /// replaces the earlier page, which is what a recompile does to every user-code page at once.
    /// </summary>
    /// <param name="target">The settings instance whose class declares its page configuration.</param>
    void Register(object target);

    /// <summary>Removes a page, for a contributor whose set changes at runtime.</summary>
    /// <param name="pageId">The page to remove.</param>
    void Remove(string pageId);
}

/// <summary>
/// The editor's settings: every registered page plus the two files behind them — one for the user,
/// one for the open project. Resolved from DI by whatever needs to read a setting, and by the panel
/// that edits them.
/// </summary>
public interface IEditorSettings : ISettingsRegistry
{
    /// <summary>Every registered page, ordered by path.</summary>
    IReadOnlyList<SettingsPageDescriptor> Pages { get; }

    /// <summary>Raised when a page is added or removed, or when values were written.</summary>
    event Action? Changed;

    /// <summary>Whether a project is open, so workspace-scoped pages have somewhere to be stored.</summary>
    bool HasWorkspace { get; }

    /// <summary>The file a scope is stored in, or an empty string when there is no project open.</summary>
    /// <param name="scope">The scope to locate.</param>
    /// <returns>An absolute path.</returns>
    string PathFor(SettingsScope scope);

    /// <summary>
    /// Points the workspace scope at a project, restoring every workspace page from it. Passing null
    /// detaches, which is what closing a project does.
    /// </summary>
    /// <param name="projectDirectory">The project's root directory, or null for none.</param>
    void BindWorkspace(string? projectDirectory);

    /// <summary>
    /// Writes every scope out now. Edits are otherwise saved as they settle, so this is for shutdown
    /// and for anything that needs the files to exist — opening one in an external editor.
    /// </summary>
    void Save();

    /// <summary>
    /// Reports that a page's values were edited, so the change is persisted and anything following
    /// <see cref="Changed"/> can react.
    /// </summary>
    /// <param name="pageId">The page that changed.</param>
    void NotifyChanged(string pageId);
}
