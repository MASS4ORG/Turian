namespace Gaya.Plugin.Turian;

/// <summary>
/// Shows the per-user settings of the export platforms in the settings panel. Platforms other than desktop come from
/// a project's bricks, which load after Studio starts, so the pages are published again whenever new brick assemblies
/// arrive.
/// </summary>
sealed class PlatformSettingsBridge(ISettingsRegistry settings)
{
    int published = -1;

    /// <summary>Publishes the platforms' pages if bricks were loaded since the last call. Cheap when none were.</summary>
    public void Sync()
    {
        var generation = BrickAssemblies.Generation;
        if (generation == published)
        {
            return;
        }

        published = generation;
        // Registering a page again replaces it, keeping the values already filled into the platform's instance.
        foreach (var page in BuildPlatforms.All().Select(static platform => platform.UserSettings).OfType<object>())
        {
            settings.Register(page);
        }
    }
}
