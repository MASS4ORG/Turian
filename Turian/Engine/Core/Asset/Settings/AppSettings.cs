namespace Turian.Engine.Core;

/// <inheritdoc cref="IAppSettings"/>
[InternalService(InternalServiceLifetime.Singleton, typeof(IAppSettings))]
public class AppSettings : IAppSettings
{
    /// <inheritdoc/>
    public string ProjectAbsoluteDir { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string AssetsAbsoluteDir =>
        ProjectAbsoluteDir.Length == 0 ? string.Empty : Path.Combine(Path.GetFullPath(ProjectAbsoluteDir), "Assets");

    /// <inheritdoc/>
    public string? Title { get; set; }

    /// <inheritdoc/>
    public ProjectSettingsSet Loaded { get; } = new();

    /// <inheritdoc/>
    public T Get<T>()
        where T : ProjectSettingsAsset, new() => Loaded.Get<T>();

    /// <inheritdoc/>
    public string TitleToPathFriendly
    {
        get
        {
            if (string.IsNullOrEmpty(Title))
            {
                return string.Empty;
            }
            var invalidChars = Path.GetInvalidFileNameChars().Union(Path.GetInvalidPathChars()).ToArray();
            return new string([.. Title.Select(ch => invalidChars.Contains(ch) ? '_' : ch)]);
        }
    }

    /// <inheritdoc/>
    public IAppSettings Load(IAppSettings appSettings)
    {
        ArgumentNullException.ThrowIfNull(appSettings);
        if (ReferenceEquals(appSettings, this)) return this;

        ProjectAbsoluteDir = appSettings.ProjectAbsoluteDir;
        Title = appSettings.Title;
        Loaded.Clear();
        Loaded.UseAll(appSettings.Loaded);
        return this;
    }
}
