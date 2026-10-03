namespace Turian.Tests;

/// <summary>Tests the loaders that read project settings and input actions through an <see cref="IAssetLoader"/>.</summary>
public sealed class SettingsLoaderTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-settings-loader").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    static AppSettings WithActions(Guid? actionsId)
    {
        var settings = new AppSettings();
        settings.Loaded.Use(new InputSettings
        {
            Actions = actionsId is { } id ? new AssetReference<DataAssetAsset>(id) : null,
        });
        return settings;
    }

    /// <summary>The configured action maps are the loader's shared payload for the configured id.</summary>
    [Fact]
    public void InputActions_ResolveThroughTheLoader()
    {
        var id = Guid.NewGuid();
        var maps = new InputActionsAsset();
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<InputActionsAsset>(id).Returns(maps);

        Assert.Same(maps, InputActionsLoader.Resolve(WithActions(id), loader));
    }

    /// <summary>No settings, no configured asset or no loader resolves to nothing.</summary>
    [Fact]
    public void InputActions_ResolveToNullWithoutConfigurationOrLoader()
    {
        var loader = Substitute.For<IAssetLoader>();

        Assert.Null(InputActionsLoader.Resolve(null, loader));
        Assert.Null(InputActionsLoader.Resolve(WithActions(null), loader));
        Assert.Null(InputActionsLoader.Resolve(WithActions(Guid.NewGuid()), null));
    }

    /// <summary>An unreadable asset is reported and resolves to nothing instead of failing the caller.</summary>
    [Fact]
    public void InputActions_UnreadableAssetResolvesToNull()
    {
        var id = Guid.NewGuid();
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<InputActionsAsset>(id).Returns<Task<InputActionsAsset?>>(_ => throw new IOException("gone"));

        Assert.Null(InputActionsLoader.Resolve(WithActions(id), loader));
    }

    /// <summary>A built game reads each indexed settings asset through the loader; unreadable ones are skipped.</summary>
    [Fact]
    public void ProjectSettings_LoadIndexedAssetsThroughTheLoader()
    {
        var readable = Guid.NewGuid();
        var broken = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, ProjectSettingsLoader.IndexFileName),
            JsonSerializer.Serialize(new { Settings = new[] { broken, readable } }));
        var input = new InputSettings();
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<ProjectSettingsAsset>(readable).Returns(input);
        loader.LoadContentAsync<ProjectSettingsAsset>(broken).Returns<Task<ProjectSettingsAsset?>>(_ => throw new IOException("gone"));
        var settings = new AppSettings { ProjectAbsoluteDir = root };

        ProjectSettingsLoader.Load(settings, loader);

        Assert.Same(input, settings.Get<InputSettings>());
    }

    /// <summary>With an index but no loader, nothing is loaded and nothing throws.</summary>
    [Fact]
    public void ProjectSettings_WithoutLoaderLoadNothing()
    {
        File.WriteAllText(Path.Combine(root, ProjectSettingsLoader.IndexFileName), """{ "Settings": [] }""");
        var settings = new AppSettings { ProjectAbsoluteDir = root };

        ProjectSettingsLoader.Load(settings, loader: null);

        Assert.NotNull(settings.Get<InputSettings>());
    }
}
