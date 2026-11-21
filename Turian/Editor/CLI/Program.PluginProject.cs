using System.Threading;
using Turian.Cli.Sdk;

namespace Turian.Editor.CLI;

public static partial class Program
{
    internal static async Task<ICliProject> OpenPluginProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = CreateSettings(new DirectoryInfo(projectPath));
        var build = await PrepareProjectAsync(settings, reimport: false, loadUserCode: true,
            requireUserCode: true).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = new AssetDatabase();
            var packages = ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir);
            database.SetPackageRoots(settings.ProjectAbsoluteDir, packages.Select(static package => package.RootPath));
            database.BuildDatabase(settings.AssetsAbsoluteDir);
            var registrations = new ServiceCollection()
                .AddSingleton<IAppSettings>(settings)
                .AddSingleton(database)
                .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database));
            var modules = build?.ActiveUserAssembly is { } userAssembly
                ? EngineServiceModules.Graph(userAssembly)
                : EngineServiceModules.LoadedGraph();
            registrations.AddEngineModules([.. modules]);
            return new PluginProject(registrations.BuildServiceProvider(), build);
        }
        catch
        {
            build?.Dispose();
            throw;
        }
    }

    sealed class PluginProject(ServiceProvider services, BuildManager? build) : ICliProject
    {
        public IServiceProvider Services => services;

        public void Dispose()
        {
            try
            {
                services.Dispose();
            }
            finally
            {
                build?.Dispose();
            }
        }
    }
}
