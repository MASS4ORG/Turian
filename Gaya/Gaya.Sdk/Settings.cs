using System.Runtime.CompilerServices;

[assembly: TypeForwardedTo(typeof(Gaya.Sdk.SettingsScope))]
[assembly: TypeForwardedTo(typeof(Gaya.Sdk.SettingsScopes))]

namespace Gaya.Sdk;

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
        {
            throw new ArgumentException("Settings classes must declare a category path.", nameof(target));
        }

        Target = target;
        Id = attribute.IdFor(type);
        Path = attribute.Path.Trim();
        Scope = attribute.Scope != SettingsScope.None ? HomeOf(attribute.Scope)
            : attribute.Workspace ? SettingsScope.Workspace : SettingsScope.User;
        Scopes = Scope | attribute.Scopes;
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

    /// <summary>
    /// The page's home scope: the tab that lists it, and where a value that no scope holds yet is
    /// written when it is first edited.
    /// </summary>
    public SettingsScope Scope { get; }

    /// <summary>Every scope that may hold this page's values; always includes <see cref="Scope"/>.</summary>
    public SettingsScope Scopes { get; }

    /// <summary>Whether a value of this page may be stored in <paramref name="scope"/>.</summary>
    /// <param name="scope">A single scope.</param>
    public bool Allows(SettingsScope scope) => scope != SettingsScope.None && (Scopes & scope) == scope;

    /// <summary>Whether the page can be stored in more than one scope, so its members cascade.</summary>
    public bool IsLayered => (Scopes & (Scopes - 1)) != 0;

    /// <summary>The scopes that may hold this page, lowest precedence first.</summary>
    public IEnumerable<SettingsScope> Layers => SettingsScopes.Ascending.Where(Allows);

    static SettingsScope HomeOf(SettingsScope declared) =>
        SettingsScopes.Ascending.FirstOrDefault(scope => (declared & scope) == scope && declared != SettingsScope.None);

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

/// <summary>What a scope operation did: whether it took effect, and where the value now resolves from.</summary>
/// <param name="Succeeded">Whether the requested change was made in full.</param>
/// <param name="Effective">
/// The scope the member resolves from afterwards, or null when no scope holds it and the page's own
/// default applies. Reported even on failure, so a caller can show where the value actually is.
/// </param>
/// <param name="Error">Why it failed, or an empty string.</param>
public readonly record struct SettingsChange(bool Succeeded, SettingsScope? Effective, string Error = "");

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
        {
            values[property.Name] = Read(() => property.GetValue(target));
        }

        foreach (var field in target.GetType().GetFields(scope).Where(f => !f.IsInitOnly))
        {
            values[field.Name] = Read(() => field.GetValue(target));
        }

        return values;
    }

    /// <summary>Puts every member a snapshot names back to the value it holds.</summary>
    /// <param name="target">The object to write to.</param>
    /// <param name="defaults">What <see cref="Capture"/> read from it.</param>
    public static void Restore(object target, IReadOnlyDictionary<string, object?> defaults)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(defaults);

        foreach (var (name, value) in defaults)
        {
            try
            {
                if (target.GetType().GetProperty(name, scope) is { CanWrite: true } property)
                {
                    property.SetValue(target, value);
                }
                else if (target.GetType().GetField(name, scope) is { IsInitOnly: false } field)
                {
                    field.SetValue(target, value);
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or ArgumentException)
            {
                // A setter that rejects its own declared default has nothing sensible to restore.
            }
        }
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

    /// <summary>Whether a project identity is bound, so personal-scoped values have somewhere to be stored.</summary>
    bool HasPersonal { get; }

    /// <summary>The file a scope is stored in, or an empty string when the scope is not bound.</summary>
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
    /// Points the personal scope at a project by its identity, which keys the files so a renamed or moved project keeps them.
    /// Null, or anything that is not a GUID, detaches.
    /// </summary>
    /// <param name="projectId">The project's identity, or null for none.</param>
    void BindPersonal(string? projectId);

    /// <summary>The highest scope that holds a member, which is where its value comes from.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <returns>The scope, or null when no scope holds it and the page's own default applies.</returns>
    SettingsScope? EffectiveScope(string pageId, string member);

    /// <summary>Every scope that holds a member, lowest precedence first.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    IReadOnlyList<SettingsScope> ScopesHolding(string pageId, string member);

    /// <summary>Reads what one scope holds for a member, ignoring the other scopes.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="scope">The scope to read.</param>
    /// <param name="value">The stored value, converted to the member's type.</param>
    /// <returns>Whether the scope holds the member.</returns>
    bool TryRead(string pageId, string member, SettingsScope scope, out object? value);

    /// <summary>
    /// Stores a member's value in an explicit scope, which then overrides every lower scope.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="scope">The scope to write; the page has to allow it.</param>
    /// <param name="value">The value, convertible to the member's type.</param>
    SettingsChange Write(string pageId, string member, SettingsScope scope, object? value);

    /// <summary>
    /// Stores a copy of what one scope holds in another, leaving the source as it is.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="from">The scope that holds the value.</param>
    /// <param name="to">The scope to copy it to.</param>
    SettingsChange Copy(string pageId, string member, SettingsScope from, SettingsScope to);

    /// <summary>
    /// Moves a value between scopes. The destination is written before the source is cleared, so a
    /// failure or a crash between the two leaves the same value in both, never in neither.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="from">The scope that holds the value.</param>
    /// <param name="to">The scope to move it to.</param>
    SettingsChange Move(string pageId, string member, SettingsScope from, SettingsScope to);

    /// <summary>Removes a member from a scope, so it falls through to the next scope below.</summary>
    /// <param name="pageId">The page.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="scope">The scope to clear.</param>
    SettingsChange Reset(string pageId, string member, SettingsScope scope);

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
