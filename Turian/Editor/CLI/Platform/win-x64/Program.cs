namespace Turian;

public sealed class RuntimeProjectOptions
{
    public string ProjectPath { get; init; } = string.Empty;
}

static class Program
{
    static void Main(string[] args)
    {
        var projectPath = GetProjectPath();

        // Create Host. The default builder wires console logging (ILoggerFactory/ILogger<T>) out
        // of the box; only the non-generic ILogger our engine constructors take needs adding.
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(
                (_, services) => services
                        .AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Turian"))
                        .AddSingleton(new RuntimeProjectOptions
                        {
                            ProjectPath = projectPath
                        })

                        // Managers
                        .AddSingleton<WindowManager>()
                        .AddSingleton<InputManager>()
                        .AddSingleton<AssetDatabase>()
                        .AddSingleton<Vulkan>()
                        .AddSingleton<RendererManager>()

                        // App instance
                        .AddScoped<App>())
            .Build();

        Log.Configure(host.Services.GetRequiredService<ILoggerFactory>());
        Log.Logger.RestartTimer();
        Log.Logger.Lap("startup", "services registered");

        // Use our service
        using var serviceScope = host.Services.CreateScope();
        var services = serviceScope.ServiceProvider;
        RuntimeServices.Configure(services);

        try
        {
            var app = services.GetRequiredService<App>();
            app.Initialize();
        }
        catch (Exception ex)
        {
            Log.Logger.LogCritical(ex, "Unhandled exception during app initialization");
            throw;
        }

        Log.Shutdown();
    }

    /// <summary>
    /// The folder the game runs from: the one its build wrote the settings index into — beside the
    /// executable, else the working directory — or a project folder when run from sources.
    /// </summary>
    static string GetProjectPath()
    {
        foreach (var candidate in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (File.Exists(Path.Combine(candidate, ProjectSettingsLoader.IndexFileName)))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return SettingsService.ResolveProjectDirectory(Directory.GetCurrentDirectory())
               ?? throw new DirectoryNotFoundException(
                   $"Could not find {ProjectSettingsLoader.IndexFileName} beside the executable "
                   + "or in the working directory.");
    }
}
