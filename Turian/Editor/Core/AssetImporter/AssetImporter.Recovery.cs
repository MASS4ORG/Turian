namespace Turian.Editor.Core;

public sealed partial class AssetImporter
{
    /// <summary>Repairs obsolete model catalogs and missing cached artifacts before a headless host loads assets.</summary>
    /// <returns>Whether the catalog required an asset scan.</returns>
    public static bool RepairCachedCatalog(AssetDatabase database, IAppSettings settings, ILogger logger)
    {
        if (!database.GetAssetsSnapshot().Any(NeedsCatalogRepair)) return false;

        logger.LogInformation("Refreshing obsolete or incomplete asset cache under {Project}",
            settings.ProjectAbsoluteDir);
        using var importer = new AssetImporter(logger, database, new SettingsService());
        importer.GenerateMetaFiles(settings.AssetsAbsoluteDir);
        return true;
    }

    static bool NeedsCatalogRepair(AssetRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ImportedRelativePath)) return false;
        if (!File.Exists(Path.Combine(record.ProjectRootPath, record.SourceRelativePath))) return false;
        return record.ImportedRelativePath.EndsWith(".ammesh", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(record.ResolveContentPath());
    }
}
