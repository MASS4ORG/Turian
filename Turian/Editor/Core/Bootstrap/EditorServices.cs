namespace Turian.Editor.Core;

/// <summary>
/// Registers the framework-agnostic editor services from <c>Turian.Editor.Core</c> that the game
/// studio panels resolve.
/// </summary>
public static class EditorServices
{
    /// <summary>Adds the editor services and every <c>[InternalService]</c> type behind them.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="logger">Logger shared with the engine.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddEditorServices(this IServiceCollection services, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(logger);
        services.AddSingleton<IAppSettings>(_ => new AppSettings());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ProjectBootstrapper>();
        services.AddSingleton<BuildManager>();
        services.AddSingleton<AssetDatabase>();
        services.AddSingleton<AssetManager>();
        services.AddSingleton<NodeInspectorController>();
        services.AddSingleton<SceneTreeController>();

        // A forwarding factory, not a second registration: the play host must be the same instance
        // the scene tree uses.
        services.AddSingleton<IPlaySceneHost>(sp => sp.GetRequiredService<SceneTreeController>());

        services.AddSingleton<AssetFileSystem>();
        services.AddSingleton<AssetWorkspace>();
        services.AddSingleton<WorkspaceSessionStore>();
        services.AddSingleton<SceneDocumentBinder>();
        services.AddSingleton(sp => new Vulkan(sp.GetRequiredService<ILogger>()));
        services.AddSingleton<PlayModeService>();

        return services.AddInternalServices(
            typeof(RuntimeServices).Assembly,
            typeof(UiDocumentComponent).Assembly,
            typeof(SceneTreeController).Assembly);
    }
}
