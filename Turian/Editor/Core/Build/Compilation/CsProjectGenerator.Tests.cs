using Turian.Testing;

namespace Turian.Editor.Core;

public static partial class CsProjectGenerator
{
    /// <summary>Generates a separate test project for sources under the project's top-level Tests folder.</summary>
    /// <param name="settings">The user project.</param>
    /// <param name="logger">Receives generation diagnostics.</param>
    /// <returns>The test project; referenced user-code projects are saved alongside their normal generated outputs.</returns>
    public static ProjectRootElement GenerateTests(IBuildAppSettings settings, ILogger logger)
    {
        var graph = DiscoverAssemblies(settings);
        foreach (var dependency in GenerateUserCodeProjects(settings, logger, graph)) { GeneratedProject.Save(dependency); }
        var project = ProjectRootElement.Create(Path.Combine(settings.CacheAbsoluteDir, "Tests", "Turian.UserTests.csproj"));
        project.Sdk = settings.TargetSdk;
        var properties = project.AddPropertyGroup();
        properties.AddProperty("TargetFramework", settings.TargetFramework);
        properties.AddProperty("AssemblyName", (settings.TitleToPathFriendly ?? "Usercode") + ".Tests");
        properties.AddProperty("EnableDefaultCompileItems", "false");
        properties.AddProperty("ImplicitUsings", "enable");
        properties.AddProperty("Nullable", "enable");
        properties.AddProperty("DebugType", "portable");
        properties.AddProperty("IsPackable", "false");
        var items = project.AddItemGroup();
        items.AddItem("Compile", Path.Combine(settings.ProjectAbsoluteDir, "Tests", "**", "*.cs"))
            .Exclude = "**/obj/**;**/bin/**;**/*~/**";
        items.AddItem("ProjectReference", UserCodeCsProjFullPath(settings));
        foreach (var assembly in graph.Definitions.Where(static assembly => !assembly.EditorOnly))
        {
            items.AddItem("ProjectReference", AssemblyCsProjFullPath(settings, assembly.Name));
        }
        items.AddItem("Reference", "Turian.Testing").AddMetadata("HintPath", typeof(TestScene).Assembly.Location, true);
        project.AddTurianDir().AddInternalDllReferences(settings).AddNugetPackageReferences(settings.PackageReferences);
        return project;
    }
}
