namespace Turian.Tests;

/// <summary>Android export contracts independent of the Android workload and devices.</summary>
public sealed class AndroidBuildPlatformTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "turian-android-" + Guid.NewGuid().ToString("N"));

    /// <summary>Removes the isolated APK fixtures.</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Export properties select the supported OS, CoreCLR, stored content and an installable APK.</summary>
    [Fact]
    public void ConfiguresAndroidExport()
    {
        Directory.CreateDirectory(directory);
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory, Title = "Example" };
        settings.Loaded.Use(new PlayerSettings { ApplicationIdentifier = "org.example.app", ProductName = "Example" });
        var project = Microsoft.Build.Construction.ProjectRootElement.Create(Path.Combine(directory, "app.csproj"));
        var platform = new AndroidBuildPlatform();
        platform.ConfigureProject(project, new BuildPlatformContext(settings, NullLogger.Instance,
            project.FullPath, directory, "Debug", new BuildProfile { Platform = "android" }));
        var properties = project.Properties.ToDictionary(static p => p.Name, static p => p.Value);

        Assert.Equal("net11.0-android", properties["TargetFramework"]);
        Assert.Equal("33", properties["SupportedOSPlatformVersion"]);
        Assert.Equal("false", properties["UseMonoRuntime"]);
        Assert.Equal("false", properties["PublishTrimmed"]);
        Assert.Equal("org.example.app", properties["ApplicationId"]);
        Assert.Equal("android-arm64", properties["RuntimeIdentifiers"]);
        Assert.Contains(".oap", properties["AndroidStoreUncompressedFileExtensions"]);
        Assert.Contains(project.Items, static item => item.ItemType == "AndroidAsset");
        foreach (var name in new[] { "Turian.Android.Host", "Turian.Engine.Vulkan" })
        {
            var reference = Assert.Single(project.Items, item => item.ItemType == "Reference" && item.Include == name);
            var hint = Assert.Single(reference.Metadata, static metadata => metadata.Name == "HintPath").Value;
            Assert.Contains("Precast~", hint);
            Assert.True(File.Exists(hint));
        }
        Assert.True(File.Exists(Path.Combine(platform.LauncherDirectory, "MainActivity.cs")));
        Assert.Contains(platform.Dependencies.Assemblies, static item => item.Item2 == "Turian.Engine.Core");
        Assert.Contains(platform.Dependencies.Assemblies, static item => item.Item2 == "Turian.Engine.Vulkan");
        Assert.Contains(project.Items, static item => item.ItemType == "PackageReference" && item.Include == "SkiaSharp.NativeAssets.Android");
    }

    /// <summary>Generated exports carry their own SDK pin and stage content before APK publication.</summary>
    [Fact]
    public async Task StagesContentBeforePublishing()
    {
        Directory.CreateDirectory(directory);
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory, Title = "Example" };
        Directory.CreateDirectory(settings.AssetsAbsoluteDir);
        Directory.CreateDirectory(settings.CacheAbsoluteDir);
        settings.Loaded.Use(new PlayerSettings { ApplicationIdentifier = "org.example.app" });
        settings.Loaded.Use(new AndroidPlayerSettings { VersionCode = 23, BackButtonQuits = false });
        var manifest = new ProjectManifest();
        manifest.Dependencies["org.mass4.turian.android"] = "builtin:org.mass4.turian.android";
        manifest.Save(directory);
        var profile = new BuildProfile { Platform = "android", RuntimeIdentifier = "android-x64" };
        var generated = CsProjectGenerator.GenerateExecutable(settings, NullLogger.Instance,
            ExecutableGenerationMode.Export, profile);
        var context = new BuildPlatformContext(settings, NullLogger.Instance, generated.FullPath,
            directory, "Debug", profile);
        var platform = new AndroidBuildPlatform();
        Assert.Contains(generated.Properties, static p => p.Name == "RuntimeIdentifiers" && p.Value == "android-x64");
        Assert.Contains(generated.Properties, static p => p.Name == "ApplicationVersion" && p.Value == "23");
        Assert.Contains(generated.Items, static item => item.ItemType == "Reference" && item.Include == "Turian.Engine.Vulkan");
        Assert.Single(generated.Items, static item => item.ItemType == "PackageReference"
            && item.Include == "SkiaSharp.NativeAssets.Android");
        var sdk = File.ReadAllText(Path.Combine(Path.GetDirectoryName(generated.FullPath)!, "global.json"));
        Assert.Contains(platform.SdkVersion!, sdk);
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.PrepareAsync(context, TestContext.Current.CancellationToken));
        File.WriteAllText(Path.Combine(settings.CacheAbsoluteDir, "assetCatalog.json"), "{\"Records\":[]}");
        File.WriteAllText(Path.Combine(settings.CacheAbsoluteDir, ProjectSettingsLoader.IndexFileName), "{}");
        await platform.PrepareAsync(context, TestContext.Current.CancellationToken);
        var assets = Path.Combine(Path.GetDirectoryName(generated.FullPath)!, "AndroidAssets");
        Assert.True(File.Exists(Path.Combine(assets, "Content", "Example.oap")));
        Assert.True(File.Exists(Path.Combine(assets, "Content", "assetCatalog.json")));
        Assert.True(File.Exists(Path.Combine(assets, "usercode.typeids.json")));
        Assert.Equal("false", File.ReadAllText(Path.Combine(assets, "android-player.json")));
        File.WriteAllText(Path.Combine(assets, "stale.oap"), "stale");
        await platform.PrepareAsync(context, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(assets, "stale.oap")));
        await Assert.ThrowsAsync<InvalidDataException>(() => platform.FinalizeAsync(context, TestContext.Current.CancellationToken));
    }

    /// <summary>Android refuses desktop execution modes, NativeAOT and unsupported architectures.</summary>
    [Theory]
    [InlineData("linux-x64", false, false)]
    [InlineData("android-arm64", true, false)]
    [InlineData("android-arm64", false, true)]
    public void RejectsUnsupportedExport(string rid, bool native, bool play)
    {
        Directory.CreateDirectory(directory);
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory };
        settings.Loaded.Use(new PlayerSettings { ApplicationIdentifier = "org.example.app" });
        var project = Microsoft.Build.Construction.ProjectRootElement.Create(Path.Combine(directory, "app.csproj"));
        var profile = new BuildProfile { RuntimeIdentifier = rid, NativeAot = native };
        Assert.Throws<InvalidOperationException>(() => new AndroidBuildPlatform().ConfigureProject(project,
            new BuildPlatformContext(settings, NullLogger.Instance, project.FullPath, directory, "Debug", profile,
                play ? ExecutableGenerationMode.Play : ExecutableGenerationMode.Export)));
    }

    /// <summary>Invalid app ids fail before publishing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("app")]
    [InlineData("org.example.bad-name")]
    public void RejectsInvalidApplicationId(string? id)
    {
        var settings = new BuildAppSettings();
        settings.Loaded.Use(new PlayerSettings { ApplicationIdentifier = id });
        Assert.NotNull(new AndroidBuildPlatform().ValidateSettings(settings));
    }

    /// <summary>APK validation checks the compression method, rather than comparing compressed lengths.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChecksStoredPackages(bool compressed)
    {
        Directory.CreateDirectory(directory);
        var apk = Path.Combine(directory, "app.apk");
        using (var archive = ZipFile.Open(apk, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "projectSettings.json", "usercode.typeids.json", "Content/assetCatalog.json" })
            {
                using var writer = new StreamWriter(archive.CreateEntry("assets/" + name).Open());
                writer.Write("{}");
            }
            using var package = archive.CreateEntry("assets/Content/example.oap",
                compressed ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
            package.Write(new byte[1024]);
        }
        if (compressed)
        {
            var error = Assert.Throws<InvalidDataException>(() => AndroidPackageValidator.Validate(apk));
            Assert.Contains("AndroidStoreUncompressedFileExtensions", error.Message);
        }
        else
        {
            AndroidPackageValidator.Validate(apk);
        }
    }

    /// <summary>An APK without runtime content cannot pass export validation.</summary>
    [Fact]
    public void RejectsMissingRuntimeContent()
    {
        Directory.CreateDirectory(directory);
        var apk = Path.Combine(directory, "empty.apk");
        using (ZipFile.Open(apk, ZipArchiveMode.Create))
        {
        }
        Assert.Throws<InvalidDataException>(() => AndroidPackageValidator.Validate(apk));
    }
}
