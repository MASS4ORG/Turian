namespace Turian.Engine.Core;

/// <summary>
/// Loads a project's string table assets and the active/default locale from <see cref="LocalizationSettings"/>
/// into a <see cref="LocaleService"/>. The entry point hosts use when a project, play scope or game
/// boots, so every context gets the same behaviour: tables from the asset database, locale from settings.
/// </summary>
/// <remarks>
/// Runs after the project settings are loaded and the asset catalog is indexed. Loading is additive and
/// permissive: a broken table is skipped with a warning rather than failing the host, so a project with
/// one damaged <c>.strings</c> file still runs the rest localized.
/// </remarks>
public static class LocalizationLoader
{
    /// <summary>Creates a service for a project and populates it.</summary>
    /// <param name="settings">The project settings to read locale defaults from, or <c>null</c>.</param>
    /// <param name="database">The catalog to load string table assets from, or <c>null</c>.</param>
    /// <returns>A ready-to-use service.</returns>
    public static LocaleService Create(IAppSettings? settings, AssetDatabase? database) =>
        Load(new LocaleService(DefaultLocaleOf(settings)), settings, database);

    /// <summary>
    /// Replaces the service's tables from the catalog and reselects the active locale. Safe to call
    /// more than once: loading a project after switching reverts to the settings' active locale.
    /// </summary>
    /// <param name="service">The service to populate in place.</param>
    /// <param name="settings">The project settings to read locale defaults from, or <c>null</c>.</param>
    /// <param name="database">The catalog to load string table assets from, or <c>null</c>.</param>
    /// <returns><paramref name="service"/>.</returns>
    public static LocaleService Load(LocaleService service, IAppSettings? settings, AssetDatabase? database)
    {
        ArgumentNullException.ThrowIfNull(service);

        service.ClearTables();

        var localization = settings?.Get<LocalizationSettings>();
        if (!string.IsNullOrWhiteSpace(localization?.DefaultLocale))
            service.SetLocale(localization.DefaultLocale);

        if (database is null) return service;

        var expectedType = typeof(StringTableAsset).FullName;
        foreach (var record in database.GetAssetsSnapshot())
        {
            if (!string.Equals(record.AssetTypeName, expectedType, StringComparison.Ordinal)) continue;

            if (!database.TryGetAssetProvider(record.AssetId, out var provider) || provider is null) continue;

            try
            {
                using var stream = provider.GetAssetStream();
                using var reader = new StreamReader(stream);
                service.AddTable(StringTableJson.Load(reader.ReadToEnd()));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Log.Logger.LogWarning(ex, "Skipping unreadable string table asset {Asset} ({RelativePath})", record.AssetId, record.SourceRelativePath);
            }
        }

        service.SetLocale(DefaultLocaleOf(settings));
        return service;
    }

    static string DefaultLocaleOf(IAppSettings? settings) =>
        settings?.Get<LocalizationSettings>().DefaultLocale ?? "en";
}
