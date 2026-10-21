using Microsoft.Build.Construction;

namespace Turian.Tests;

/// <summary>Build profiles: discovery, what client and server builds ship, and the platform that publishes them.</summary>
public sealed class BuildProfilesTests : IDisposable
{
    readonly string project = Directory.CreateTempSubdirectory("turian-build-profiles-").FullName;
    readonly string fixtures = Directory.CreateTempSubdirectory("turian-build-profiles-bricks-").FullName;

    /// <summary>Creates the project's Assets folder.</summary>
    public BuildProfilesTests() => Directory.CreateDirectory(Assets);

    string Assets => Path.Combine(project, "Assets");

    BuildAppSettings Settings => new() { Title = "Game", ProjectAbsoluteDir = project };

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.Delete(project, recursive: true);
        Directory.Delete(fixtures, recursive: true);
    }

    /// <summary>Installs source bricks made for the test; <see cref="ViewBrick"/> uses the renderer, so it is client-only.</summary>
    IReadOnlyList<ResolvedPackage> Install(params string[] ids)
    {
        var manifest = new ProjectManifest();
        foreach (var id in ids)
        {
            manifest.Dependencies[id] = $"file:{Fixture(id)}";
        }

        manifest.Save(project);
        ProjectPackages.Invalidate(project);
        return ProjectPackages.Resolve(project).Packages;
    }

    const string RulesBrick = "user.mateo.rules";
    const string ViewBrick = "user.mateo.view";

    string Fixture(string id)
    {
        var folder = Path.Combine(fixtures, id);
        if (Directory.Exists(folder))
        {
            return folder;
        }

        BrickService.New(fixtures, id);
        if (id == ViewBrick)
        {
            var manifest = PackageManifest.Load(folder, ["gaya", ProjectPackages.HostName]);
            manifest.Dependencies[ProjectPackages.RenderBrick] = $"builtin:{ProjectPackages.RenderBrick}";
            manifest.Save(folder);
        }

        return folder;
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
        var bricks = Install(RulesBrick, ViewBrick);

        var client = BuildProfiles.ShippingBricks(bricks, BuildTarget.Client).Select(static b => b.Id).Order();
        var server = BuildProfiles.ShippingBricks(bricks, BuildTarget.Server).Select(static b => b.Id);

        Assert.Equal([ProjectPackages.RenderBrick, RulesBrick, ViewBrick], client);
        Assert.Equal([RulesBrick], server);
        Assert.True(BuildProfiles.HasPlayer(BuildProfiles.ShippingBricks(bricks, BuildTarget.Client)));
        Assert.False(BuildProfiles.HasPlayer(BuildProfiles.ShippingBricks(bricks, BuildTarget.Server)));
    }

    static ResolvedPackage Brick(string id, params string[] platforms) =>
        new(id, new PackageManifest { Name = id, Platforms = [.. platforms] }, "/", PackageOrigin.Builtin, "", null, null, 1, false);

    /// <summary>A brick without a platform list ships everywhere; one with a list ships only on those platforms.</summary>
    [Fact]
    public void PlatformAllowListFailsExportNamingTheBricks()
    {
        BuildProfiles.EnsureSupported([Brick("a.any"), Brick("a.desktop", "Desktop")], BuildPlatforms.Desktop);

        var error = Assert.Throws<InvalidOperationException>(static () =>
            BuildProfiles.EnsureSupported([Brick("a.any"), Brick("a.desktop", "desktop"), Brick("a.xr", "xr")], "android"));
        Assert.Contains("a.desktop, a.xr", error.Message);
        Assert.DoesNotContain("a.any", error.Message);
    }

    /// <summary>Platform NuGet entries add to the common ones, overriding a version, for that platform only.</summary>
    [Fact]
    public void PlatformNugetAddsToTheCommonPackages()
    {
        var manifest = new PackageManifest
        {
            Nuget = { ["Common"] = "1.0.0", ["Shared"] = "1.0.0" },
            PlatformNuget = { ["android"] = new() { ["Native"] = "2.0.0", ["Shared"] = "1.1.0" } },
        };

        Assert.Equal(["Common", "Shared"], manifest.NugetFor("desktop").Keys.Order());
        var android = manifest.NugetFor("Android");
        Assert.Equal(["Common", "Native", "Shared"], android.Keys.Order());
        Assert.Equal("1.1.0", android["Shared"]);
    }

    /// <summary>Platform lists round-trip through the manifest text, and an empty platform id is rejected.</summary>
    [Fact]
    public void PlatformFieldsRoundTripAndValidate()
    {
        var json = """{"name":"a.b","version":"1.0.0","platforms":["desktop"],"platformNuget":{"desktop":{"X":"1.0.0"}}}""";
        var manifest = PackageManifest.Parse(json, "package.json");
        Assert.Equal(["desktop"], manifest.Platforms);
        Assert.Equal("1.0.0", manifest.NugetFor("desktop")["X"]);
        Assert.Equal(["desktop"], PackageManifest.Parse(manifest.ToJson(), "package.json").Platforms);

        Assert.Throws<PackageException>(static () =>
            PackageManifest.Parse("""{"name":"a.b","version":"1.0.0","platforms":[" "]}""", "package.json"));
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
        Assert.Contains("desktop", Assert.Throws<InvalidOperationException>(static () => BuildPlatforms.Find("android")).Message);
        Assert.Equal(["publish", "game.csproj", "-o", "out", "-c", "Release", "--no-restore"],
            desktop.PublishArguments(new BuildPlatformContext(Settings, NullLogger.Instance,
                "game.csproj", "out", "Release", null)));
        Assert.Equal("-p:TurianDefines=TURIAN_SERVER%3BDEMO",
            desktop.PublishArguments(new BuildPlatformContext(Settings, NullLogger.Instance,
                "game.csproj", "out", "Release",
                new BuildProfile { Target = BuildTarget.Server, ScriptingDefines = "DEMO" }))[^1]);
        Assert.Same(desktop.GetType(), BuildPlatforms.For(new BuildProfile { Target = BuildTarget.Server }).GetType());
    }

    /// <summary>A profile without symbols still passes them, empty, so the active profile's do not leak in.</summary>
    [Fact]
    public void ProfilesWithoutSymbolsReplaceTheActiveProfiles()
    {
        Assert.Equal("-p:TurianDefines=", BuildPlatforms.Find(null).PublishArguments(
            new BuildPlatformContext(Settings, NullLogger.Instance,
                "game.csproj", "out", "Release", new BuildProfile()))[^1]);
    }

    /// <summary>
    /// The editor's script projects, which the IDE opens too, compile with the active profile's symbols, defined
    /// before the constants that read them; with no active profile they define none.
    /// </summary>
    [Fact]
    public void EditorScriptProjectsFollowTheActiveProfile()
    {
        Install();
        ProjectSettingsFiles.CreateProjectAsset(Settings, [], []);
        Directory.CreateDirectory(Path.Combine(Assets, "Shared"));
        var definition = Path.Combine(Assets, "Shared", "Acme.Shared.dataasset");
        var id = Guid.NewGuid();
        File.WriteAllText(definition,
            $$"""{ "__TypeId": "{{AssemblyDefinition.TypeIdValue}}", "Name": "Acme.Shared", "Id": "{{id}}" }""");
        File.WriteAllText(definition + ".meta", $$"""
            { "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c",
              "RelativePath": "Assets/Shared/Acme.Shared.dataasset", "Id": "{{id}}" }
            """);
        Assert.All(CsProjectGenerator.GenerateUserCodeProjects(Settings, NullLogger.Instance,
                CsProjectGenerator.DiscoverAssemblies(Settings)),
            static p => Assert.DoesNotContain(p.Properties, static q => q.Name == "TurianDefines"));

        ProjectSettingsFiles.CreateBuildProfile(Settings,
            new BuildProfile { Name = "Server", Target = BuildTarget.Server, ScriptingDefines = "DEMO" }, active: true);
        var projects = CsProjectGenerator.GenerateUserCodeProjects(Settings, NullLogger.Instance,
            CsProjectGenerator.DiscoverAssemblies(Settings));

        Assert.Equal(2, projects.Count);
        Assert.All(projects, static project =>
        {
            var names = project.Properties.Select(static p => p.Name).ToList();
            Assert.Contains(project.Properties,
                static p => p is { Name: "TurianDefines", Value: "TURIAN_SERVER;DEMO" });
            Assert.True(names.IndexOf("TurianDefines") < names.IndexOf("DefineConstants"));
        });
        Assert.Contains(CsProjectGenerator.GenerateUserCode(Settings, NullLogger.Instance).Properties,
            static p => p is { Name: "TurianDefines", Value: "TURIAN_SERVER;DEMO" });
    }

    /// <summary>
    /// A server runtime project references no client-only brick or package, fails a build that would still ship one,
    /// and pins the profile's runtime identifier; a client project keeps the renderer.
    /// </summary>
    [Fact]
    public void ServerRuntimeProjectsShipNoClientOnlyFiles()
    {
        Install();
        var server = new BuildProfile { Target = BuildTarget.Server, RuntimeIdentifier = " linux-arm64 " };

        var serverProject = CsProjectGenerator.GenerateExecutable(Settings, NullLogger.Instance,
            ExecutableGenerationMode.Export, server);
        var clientProject = CsProjectGenerator.GenerateExecutable(Settings, NullLogger.Instance);

        Assert.DoesNotContain(Items(serverProject), static i => i.Include is "Turian.Engine.Vulkan" or "Silk.NET.Vulkan");
        Assert.Contains(serverProject.Targets, static t => t.Name == "VerifyServerDependencies");
        Assert.Contains(serverProject.Properties, static p => p is { Name: "RuntimeIdentifier", Value: "linux-arm64" });
        Assert.Contains(serverProject.Properties, static p => p is { Name: "OutputType", Value: "Exe" });
        Assert.Contains(clientProject.Properties, static p => p is { Name: "OutputType", Value: "WinExe" });
        Assert.Contains(Items(clientProject), static i => i.Include == "Turian.Engine.Vulkan");
        Assert.Contains(Items(clientProject), static i => i.Include == "Silk.NET.Vulkan");
        Assert.DoesNotContain(clientProject.Targets, static t => t.Name == "VerifyServerDependencies");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(serverProject.FullPath)!, "Program.cs")));
    }

    /// <summary>The executable is named for the target runtime, so a Windows game exported elsewhere still ends in .exe.</summary>
    [Fact]
    public void ExecutablesAreNamedForTheTargetRuntime()
    {
        Assert.Equal(".exe", DesktopBuildPlatform.ExecutableExtension("win-arm64"));
        Assert.Equal(".exe", DesktopBuildPlatform.ExecutableExtension(" WIN-x64 "));
        Assert.Equal("", DesktopBuildPlatform.ExecutableExtension("linux-arm64"));
        Assert.Equal("", DesktopBuildPlatform.ExecutableExtension("osx-arm64"));
        Assert.Equal(OperatingSystem.IsWindows() ? ".exe" : "", DesktopBuildPlatform.ExecutableExtension(null));
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
        Install();

        var client = UserCodeTypeManifestGenerator.Generate(Assets, NullLogger.Instance);
        var server = UserCodeTypeManifestGenerator.Generate(Assets, NullLogger.Instance, target: BuildTarget.Server);

        Assert.Contains("Turian.Engine.Vulkan", client.PrecastAssemblies);
        Assert.Empty(server.PrecastAssemblies);
        Assert.DoesNotContain(server.Types, static t => t.Assembly == "Turian.Engine.Vulkan");
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
        Install(RulesBrick);

        if (disable)
        {
            BrickService.Disable(project, ProjectPackages.RenderBrick);
        }
        else
        {
            Assert.True(BrickService.Remove(project, ProjectPackages.RenderBrick));
        }

        Assert.Null(ProjectManifest.Load(project).Manifest.Dependencies[ProjectPackages.RenderBrick]);
        Assert.Equal([RulesBrick], ProjectPackages.Resolve(project).Packages.Select(static p => p.Id));
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
            () => BrickService.Disable(project, RulesBrick)).Message);
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
