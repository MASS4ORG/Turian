namespace Turian.Editor.Core;

/// <summary>Loads a project's <c>.strings</c> tables from its asset catalog into a locale service.</summary>
/// <remarks>
/// Hosts show localized interface documents while editing, so they need the project's tables without the game's
/// localization brick being loaded. A table that cannot be read is skipped so the others still apply.
/// </remarks>
public static class ProjectStringTables
{
    const string extension = ".strings";

    /// <summary>Replaces the service's tables with the ones the catalog indexes and selects the project's locale.</summary>
    /// <param name="service">The service to populate in place.</param>
    /// <param name="database">The indexed catalog to read tables from.</param>
    /// <param name="settings">The project's settings, which the policies read the starting locale from.</param>
    /// <param name="policies">Chooses the starting locale; the service's default applies when none does.</param>
    /// <param name="log">Where an unreadable table is reported.</param>
    public static void Load(LocaleService service, AssetDatabase database, IAppSettings settings,
        IEnumerable<IProjectLocalePolicy> policies, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(log);

        service.ClearTables();

        foreach (var record in database.GetAssetsSnapshot())
        {
            if (!record.SourceRelativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !database.TryGetAssetProvider(record.AssetId, out var provider) || provider is null)
            {
                continue;
            }

            try
            {
                using var reader = new StreamReader(provider.GetAssetStream());
                service.AddTable(StringTableJson.Load(reader.ReadToEnd()));
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
            {
                log.LogWarning(ex, "Skipping unreadable string table {Asset} ({RelativePath})",
                    record.AssetId, record.SourceRelativePath);
            }
        }

        service.SetLocale(ProjectLocalePolicies.DefaultLocale(policies, settings) ?? service.DefaultLocale);
    }
}
