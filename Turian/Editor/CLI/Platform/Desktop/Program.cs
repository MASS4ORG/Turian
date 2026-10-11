namespace Turian;

static class Program
{
    static int Main(string[] args)
    {
        var projectPath = GetProjectPath();

        // The default builder wires console logging (ILoggerFactory/ILogger<T>) out of the box.
        using var host = Host.CreateDefaultBuilder(args).Build();
        Log.Configure(host.Services.GetRequiredService<ILoggerFactory>());
        Log.Logger.RestartTimer();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Turian");

        // The bricks' [TypeId] types, and the player a render brick provides, register before anything loads.
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "usercode.typeids.json");
        PrecastAssemblies.Load(manifestPath, logger);
        TypeRegistry.RegisterFromManifest(manifestPath, logger);
        Log.Logger.Lap("startup", "types registered");

        var context = new GamePlayerContext(projectPath, logger, static services => services.BuildServiceProvider(),
#if DEBUG
            Development: true);
#else
            Development: false);
#endif
        try
        {
            return GamePlayers.FindOrHeadless().Run(context);
        }
        catch (Exception ex)
        {
            Log.Logger.LogCritical(ex, "Unhandled exception while running the game");
            throw;
        }
        finally
        {
            Log.Shutdown();
        }
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
