namespace Gaya.Plugin.Turian;

/// <summary>
/// Publishes the settings pages user code contributes with <c>[SettingsPage]</c> into the editor's
/// settings, and re-publishes them whenever a recompile swaps the user assembly. The discovery itself
/// is framework-agnostic and lives in <see cref="UserSettingsCatalog"/>; this only maps pages onto
/// Gaya contributions.
/// </summary>
sealed class UserSettingsBridge : IDisposable
{
    readonly UserSettingsCatalog catalog;
    readonly ISettingsRegistry settings;
    readonly ILogger log;

    readonly List<string> published = [];

    /// <summary>Creates the bridge and follows the catalog.</summary>
    /// <param name="catalog">Supplies the discovered pages.</param>
    /// <param name="settings">Where the pages are registered.</param>
    /// <param name="log">Where a republish is reported.</param>
    public UserSettingsBridge(UserSettingsCatalog catalog, ISettingsRegistry settings, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        this.catalog = catalog;
        this.settings = settings;
        this.log = log;

        catalog.Changed += Publish;
    }

    /// <summary>
    /// Rescans if the user assembly was swapped. Called once a frame; it costs a reference comparison
    /// when nothing changed.
    /// </summary>
    public void Sync() => catalog.EnsureRefreshed();

    void Publish()
    {
        foreach (var pageId in published) settings.Remove(pageId);

        published.Clear();

        foreach (var page in catalog.Pages)
        {
            settings.Register(new SettingsPageDescriptor(page.Id, page.Path, page.Target,
                page.Workspace ? SettingsScope.Workspace : SettingsScope.User, page.Order)
            {
                Description = page.Description,
            });

            published.Add(page.Id);
        }

        log.LogDebug("User settings: published {Count} page(s)", published.Count);
    }

    /// <inheritdoc />
    public void Dispose() => catalog.Changed -= Publish;
}
