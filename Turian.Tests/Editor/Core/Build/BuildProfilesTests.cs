using Microsoft.Build.Construction;

namespace Turian.Tests;

/// <summary>Build profiles: discovery, what client and server builds ship, and the platform that publishes them.</summary>
public sealed class BuildProfilesTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-build-profiles-").FullName;

    /// <summary>Creates the project's Assets folder.</summary>
    public BuildProfilesTests() => Directory.CreateDirectory(Assets);

    string Assets => Path.Combine(project, "Assets");

    BuildAppSettings Settings => new() { Title = "Game", ProjectAbsoluteDir = project };

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    IReadOnlyList<ResolvedPackage> Install(params string[] ids)
    {
        var manifest = new ProjectManifest();
        foreach (var id in ids) manifest.Dependencies[id] = $"builtin:{id}";
        manifest.Save(project);
        ProjectPackages.Invalidate(project);
        return ProjectPackages.Resolve(project).Packages;
    }

    void SaveProfile(string file, BuildProfile profile)
    {
        var path = Path.Combine(Assets, file);
        Serializer.Save<DataAsset>(path, profile);
        File.WriteAllText(path + ".meta", "{}");
    }

    /// <summary>A server leaves out the renderer and every brick depending on it; a client ships them all.</summary>
    [Fact]
    public void ServerBuildsLeaveOutClientOnlyBricksAndTheirDependents()
    {
        var bricks = Install("org.mass4.turian.cameras", "org.mass4.turian.ui", "org.mass4.turian.hzb");

        var client = BuildProfiles.ShippingBricks(bricks, BuildTarget.Client).Select(static b => b.Id).Order();
        var server = BuildProfiles.ShippingBricks(bricks, BuildTarget.Server).Select(static b => b.Id);

        Assert.Equal(["org.mass4.turian.cameras", "org.mass4.turian.hzb", "org.mass4.turian.ui", ProjectPackages.RenderBrick],
            client);
        Assert.Equal(["org.mass4.turian.cameras"], server);
        Assert.True(BuildProfiles.HasPlayer(BuildProfiles.ShippingBricks(bricks, BuildTarget.Client)));
        Assert.False(BuildProfiles.HasPlayer(BuildProfiles.ShippingBricks(bricks, BuildTarget.Server)));
    }

    /// <summary>Profiles are found by name or file name, in name order; an unknown name lists the existing ones.</summary>
    [Fact]
    public void ProfilesAreFoundByName()
    {
        Assert.Contains("declares none", Assert.Throws<InvalidOperationException>(
            () => BuildProfiles.Find(Settings, "Server")).Message);

        SaveProfile("Server.dataasset", new BuildProfile { Target = BuildTarget.Server });
        SaveProfile("client.dataasset", new BuildProfile { Name = "Windows Client" });
        Serializer.Save<DataAsset>(Path.Combine(Assets, "Other.dataasset"), new InputActionsAsset());
        File.WriteAllText(Path.Combine(Assets, "Other.dataasset.meta"), "{}");

        Assert.Equal(["Server", "Windows Client"], BuildProfiles.Discover(Settings).Select(static p => p.Name));
        Assert.Equal(BuildTarget.Server, BuildProfiles.Find(Settings, "server").Profile.Target);
        Assert.Contains("Server, Windows Client",
            Assert.Throws<InvalidOperationException>(() => BuildProfiles.Find(Settings, "Console")).Message);
    }

    /// <summary>A server compiles scripts with TURIAN_SERVER before the profile's own symbols.</summary>
    [Fact]
    public void ServerProfilesDefineTheServerSymbol()
    {
        Assert.Equal(["TURIAN_SERVER", "DEMO", "EXTRA"],
            new BuildProfile { Target = BuildTarget.Server, ScriptingDefines = " DEMO; ;EXTRA" }.Symbols);
        Assert.Empty(new BuildProfile().Symbols);
    }

    /// <summary>The desktop platform is the default, found by id in any case, and publishes with the profile's symbols.</summary>
    [Fact]
    public void DesktopPlatformPublishesWithProfileSymbols()
    {
        var desktop = BuildPlatforms.Find(null);

        Assert.Same(desktop.GetType(), BuildPlatforms.Find("DESKTOP").GetType());
        Assert.Equal("Desktop", desktop.DisplayName);
        Assert.True(Directory.Exists(desktop.LauncherDirectory));
        Assert.Contains("desktop", Assert.Throws<InvalidOperationException>(() => BuildPlatforms.Find("android")).Message);
        Assert.Equal(["publish", "game.csproj", "-o", "out", "-c", "Release", "--no-restore"],
            desktop.PublishArguments(new BuildPlatformContext("game.csproj", "out", "Release", null)));
        Assert.Equal("-p:TurianDefines=TURIAN_SERVER%3BDEMO",
            desktop.PublishArguments(new BuildPlatformContext("game.csproj", "out", "Release",
                new BuildProfile { Target = BuildTarget.Server, ScriptingDefines = "DEMO" }))[^1]);
        Assert.Same(desktop.GetType(), BuildPlatforms.For(new BuildProfile { Target = BuildTarget.Server }).GetType());
    }

    /// <summary>
    /// A server runtime project references no client-only brick or package, fails a build that would still ship one,
    /// and pins the profile's runtime identifier; a client project keeps the renderer.
    /// </summary>
    [Fact]
    public void ServerRuntimeProjectsShipNoClientOnlyFiles()
    {
        Install("org.mass4.turian.ui");
        var server = new BuildProfile { Target = BuildTarget.Server, RuntimeIdentifier = " linux-arm64 " };

        var serverProject = CsProjectGenerator.GenerateExecutable(Settings, NullLogger.Instance,
            ExecutableGenerationMode.Export, server);
        var clientProject = CsProjectGenerator.GenerateExecutable(Settings, NullLogger.Instance);

        Assert.DoesNotContain(Items(serverProject), static i => i.Include is "Turian.Engine.Vulkan" or "Turian.Engine.UI"
            or "Silk.NET.Vulkan" or "SkiaSharp");
        Assert.Contains(serverProject.Targets, static t => t.Name == "VerifyServerDependencies");
        Assert.Contains(serverProject.Properties, static p => p is { Name: "RuntimeIdentifier", Value: "linux-arm64" });
        Assert.Contains(Items(clientProject), static i => i.Include == "Turian.Engine.Vulkan");
        Assert.Contains(Items(clientProject), static i => i.Include == "Silk.NET.Vulkan");
        Assert.DoesNotContain(clientProject.Targets, static t => t.Name == "VerifyServerDependencies");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(serverProject.FullPath)!, "Program.cs")));
    }

    /// <summary>Script projects take the profile's symbols and keep brick packages from flowing into a game.</summary>
    [Fact]
    public void ScriptProjectsTakeSymbolsAndKeepBrickPackagesPrivate()
    {
        Install();

        var scripts = CsProjectGenerator.GenerateUserCode(Settings, NullLogger.Instance);

        Assert.Contains(scripts.Properties,
            static p => p is { Name: "DefineConstants", Value: "$(DefineConstants);$(TurianDefines)" });
        var silk = Assert.Single(Items(scripts), static i => i.Include == "Silk.NET.Vulkan");
        Assert.Contains(silk.Metadata, static m => m is { Name: "PrivateAssets", Value: "all" });
    }

    /// <summary>A server's type manifest lists none of the bricks it leaves out.</summary>
    [Fact]
    public void ServerTypeManifestsListNoClientOnlyAssemblies()
    {
        Install("org.mass4.turian.ui");

        var client = UserCodeTypeManifestGenerator.Generate(Assets, NullLogger.Instance);
        var server = UserCodeTypeManifestGenerator.Generate(Assets, NullLogger.Instance, target: BuildTarget.Server);

        Assert.Contains("Turian.Engine.Vulkan", client.PrecastAssemblies);
        Assert.Empty(server.PrecastAssemblies);
        Assert.DoesNotContain(server.Types, static t => t.Assembly == "Turian.Engine.UI");
    }

    /// <summary>
    /// Every project uses the render brick without declaring it; removing or disabling it opts out instead of
    /// letting the default return.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingTheRenderBrickOptsOutOfTheDefault(bool disable)
    {
        Assert.Contains(ProjectPackages.ResolveOrEmpty(project), static p => p.Id == ProjectPackages.RenderBrick);
        Install("org.mass4.turian.cameras");

        if (disable) BrickService.Disable(project, ProjectPackages.RenderBrick);
        else Assert.True(BrickService.Remove(project, ProjectPackages.RenderBrick));

        Assert.Null(ProjectManifest.Load(project).Manifest.Dependencies[ProjectPackages.RenderBrick]);
        Assert.Equal(["org.mass4.turian.cameras"], ProjectPackages.Resolve(project).Packages.Select(static p => p.Id));
    }

    /// <summary>Only a client build left without a render brick is warned that it runs headless.</summary>
    [Fact]
    public void ClientBuildsWithoutARendererAreFlagged()
    {
        Install();
        Assert.False(ExportUserCode.RunsHeadlessByAccident(project, null));

        Assert.True(BrickService.Remove(project, ProjectPackages.RenderBrick));

        Assert.True(ExportUserCode.RunsHeadlessByAccident(project, new BuildProfile()));
        Assert.False(ExportUserCode.RunsHeadlessByAccident(project, new BuildProfile { Target = BuildTarget.Server }));
    }

    /// <summary>Disabling a brick the project neither declares nor gets by default is refused.</summary>
    [Fact]
    public void DisablingAnUndeclaredBrickIsRefused()
    {
        Install();

        Assert.Contains("does not declare", Assert.Throws<PackageException>(
            () => BrickService.Disable(project, "org.mass4.turian.cameras")).Message);
    }

    /// <summary>A package cannot ship only in clients while also being editor- or compile-time-only.</summary>
    [Fact]
    public void ClientOnlyPackagesMustShip()
    {
        var folder = Path.Combine(project, "brick");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PackageManifest.FileName),
            """{ "name": "user.mateo.view", "version": "1.0.0", "clientOnly": true, "editorOnly": true }""");

        Assert.Contains("client-only", Assert.Throws<PackageException>(() => PackageManifest.Load(folder)).Message);
    }

    static IEnumerable<ProjectItemElement> Items(ProjectRootElement project) => project.Items;
}
