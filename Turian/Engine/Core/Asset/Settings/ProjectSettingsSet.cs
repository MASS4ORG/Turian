namespace Turian.Engine.Core;

/// <summary>
/// The settings assets a project has in force, one per type. A type the project does not list reads as
/// a fresh instance of itself, so its declared defaults apply until someone authors the asset.
/// </summary>
public sealed class ProjectSettingsSet
{
    readonly Dictionary<Type, ProjectSettingsAsset> byType = [];

    /// <summary>The settings of one kind, or its defaults when the project lists none.</summary>
    /// <typeparam name="T">The settings kind.</typeparam>
    /// <returns>The same instance on every call until <see cref="Use"/> replaces it.</returns>
    public T Get<T>()
        where T : ProjectSettingsAsset, new()
    {
        if (byType.TryGetValue(typeof(T), out var found)) return (T)found;

        var defaults = new T();
        byType[typeof(T)] = defaults;
        return defaults;
    }

    /// <summary>Puts a loaded settings asset in force, replacing whatever its kind held.</summary>
    /// <param name="settings">The loaded asset.</param>
    public void Use(ProjectSettingsAsset settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        byType[settings.GetType()] = settings;
    }

    /// <summary>Copies every kind another set holds into this one.</summary>
    /// <param name="other">The set to copy from.</param>
    public void UseAll(ProjectSettingsSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var settings in other.byType.Values) Use(settings);
    }

    /// <summary>Forgets every kind, so each reads as its defaults again.</summary>
    public void Clear() => byType.Clear();
}
