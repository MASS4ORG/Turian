namespace Turian.Engine.Core;

/// <summary>
/// Resolves the <see cref="InputActionsAsset"/> a project names in
/// <see cref="InputSettings.Actions"/>. The standalone runtime and the Studio's play mode both
/// go through this, so both games read the same maps.
/// </summary>
public static class InputActionsLoader
{
    /// <summary>
    /// Loads the project's action maps, or null when none are configured or the asset cannot be read.
    /// </summary>
    /// <param name="settings">The project settings naming the asset.</param>
    /// <param name="loader">Resolves the asset metadata by id.</param>
    /// <param name="projectDirectory">Root the asset's relative path is resolved against.</param>
    /// <returns>The maps, or null.</returns>
    public static InputActionsAsset? Resolve(IAppSettings? settings, IAssetLoader? loader,
        string? projectDirectory)
    {
        if (settings?.Get<InputSettings>().Actions is not { IsEmpty: false } reference) return null;
        if (loader is null || string.IsNullOrWhiteSpace(projectDirectory)) return null;

        try
        {
            return reference.LoadAsync(loader).GetAwaiter().GetResult()?.GetContent(projectDirectory)
                as InputActionsAsset;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FileNotFoundException)
        {
            Log.Logger.LogWarning(ex, "Input actions asset {AssetId} could not be loaded", reference.AssetId);
            return null;
        }
    }
}
