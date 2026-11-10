using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Build.Construction;
using Microsoft.Extensions.Logging;
using Turian.Engine.Core;

[assembly: Turian.Editor.Core.BuildPlatform(typeof(Turian.Editor.Core.AndroidBuildPlatform))]

namespace Turian.Editor.Core;

/// <summary>Exports an Android CoreCLR application with its content stored inside the APK.</summary>
public sealed partial class AndroidBuildPlatform : IBuildPlatform
{
    const string ValidationBrickPrefix = "org.mass4.turian.android.validation.";

    static readonly Lazy<RuntimeDependencies> Graph = new(LoadDependencies);

    /// <inheritdoc />
    public string Id => "android";

    /// <inheritdoc />
    public string DisplayName => "Android";

    /// <inheritdoc />
    public IReadOnlyList<BuildTarget> Targets { get; } = [BuildTarget.Client];

    /// <inheritdoc />
    public string TargetFramework => "net11.0-android";

    /// <inheritdoc />
    public string TextureTarget => TextureBuildTarget.Android;

    /// <inheritdoc />
    public string? SdkVersion => typeof(AndroidBuildPlatform).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(static metadata => metadata.Key == "TurianAndroidSdk").Value;

    static string BrickDirectory => Path.Combine(ProjectPackages.BuiltinDirectory, "org.mass4.turian.android");

    /// <inheritdoc />
    public IReadOnlyList<string> RuntimeIdentifiers { get; } = ["android-arm64", "android-x64"];

    /// <inheritdoc />
    public RuntimeDependencies Dependencies => Graph.Value;

    /// <summary>The user's Android choices, one instance that Studio and the CLI fill in place.</summary>
    public static AndroidToolchainSettings Settings { get; } = new();

    /// <inheritdoc />
    public object UserSettings => Settings;

    /// <inheritdoc />
    public bool Deploys => true;

    /// <inheritdoc />
    public BuildDeployment? ConfiguredDeployment => Settings.InstallOnDevice ? new BuildDeployment(null) : null;

    static string? Sdk => AndroidToolchain.FindSdk(Settings.SdkDirectory);

    static string? Jdk => AndroidToolchain.FindJdk(Settings.JdkDirectory);

    /// <inheritdoc />
    public IReadOnlyList<BuildDevice>? Devices
    {
        get
        {
            if (AndroidToolchain.FindAdb(Sdk) is not { } adb)
            {
                return [];
            }

            try
            {
                return AndroidToolchain.ListDevices(adb);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException
                                                  or System.ComponentModel.Win32Exception)
            {
                return [];
            }
        }
    }

    /// <inheritdoc />
    public string LauncherDirectory
    {
        get
        {
            var directory = Path.GetDirectoryName(typeof(AndroidBuildPlatform).Assembly.Location)!;
            var brickLauncher = Path.Combine(BrickDirectory, "Launcher~");
            return Directory.Exists(brickLauncher) ? brickLauncher : Path.Combine(directory, "AndroidLauncher");
        }
    }

    /// <inheritdoc />
    public void ConfigureProject(ProjectRootElement project, BuildPlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(context);
        var rid = ValidateExportContext(context);
        var manifest = MASS4.Bricks.PackageManifest.Load(BrickDirectory, ["turian"]);
        foreach (var (id, version) in manifest.PlatformNuget[Id])
        {
            project.AddItemGroup().AddItem("PackageReference", id).AddMetadata("Version", version, true);
        }
        ReferenceRuntimeAssemblies(project, context.Settings, manifest);
        ConfigureProperties(project, context, rid);
        project.AddItemGroup().AddItem("AndroidResource", AndroidIcons.ResourceGlob);
        var assets = project.AddItemGroup().AddItem("AndroidAsset", "AndroidAssets/**/*");
        assets.AddMetadata("LogicalName",
            "$([System.String]::Copy('%(RecursiveDir)%(Filename)%(Extension)').Replace('\\', '/'))", true);
        var nativeDirectory = Path.Combine(BrickDirectory, "Native~");
        if (!Directory.Exists(nativeDirectory))
        {
            nativeDirectory = Path.Combine(Path.GetDirectoryName(typeof(AndroidBuildPlatform).Assembly.Location)!,
                "AndroidNative");
        }
        project.AddImport(Path.Combine(nativeDirectory, "AndroidNative.targets"));
        if (context.Profile?.Development == true)
        {
            AddValidationLayer(project, context, string.IsNullOrEmpty(rid) ? "android-arm64" : rid);
        }
    }

    /// <summary>Packages the validation layer from the project's brick for the exported architecture, when installed.</summary>
    static void AddValidationLayer(ProjectRootElement project, BuildPlatformContext context, string runtimeIdentifier)
    {
        var (architecture, abi) = runtimeIdentifier == "android-x64" ? ("x64", "x86_64") : ("arm64", "arm64-v8a");
        var id = ValidationBrickPrefix + architecture;
        var brick = ProjectPackages.ResolveOrEmpty(context.Settings.ProjectAbsoluteDir)
            .FirstOrDefault(package => package.Id == id);
        if (brick is null)
        {
            context.Logger.LogInformation(
                "This Android development build has no Vulkan validation layer; add the {Brick} brick to include it.", id);
            return;
        }
        var layer = Path.Combine(brick.RootPath, "Brick~", "Native", AndroidPackageValidator.ValidationLayerFile);
        if (!File.Exists(layer))
        {
            throw new InvalidOperationException($"The {id} brick has no {AndroidPackageValidator.ValidationLayerFile}.");
        }
        project.AddItemGroup().AddItem("AndroidNativeLibrary", layer).AddMetadata("Abi", abi, true);
    }

    string? ValidateExportContext(BuildPlatformContext context)
    {
        if (ValidateSettings(context.Settings) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }
        if (context.Mode != ExecutableGenerationMode.Export || context.Profile?.NativeAot == true)
        {
            throw new InvalidOperationException("Android requires an exported CoreCLR client profile.");
        }

        return ValidateRuntimeIdentifier(context.Profile?.RuntimeIdentifier);
    }

    string? ValidateRuntimeIdentifier(string? runtimeIdentifier)
    {
        var rid = runtimeIdentifier?.Trim();
        if (!string.IsNullOrEmpty(rid) && !RuntimeIdentifiers.Contains(rid))
        {
            throw new InvalidOperationException($"Unsupported Android runtime identifier '{rid}'.");
        }

        return rid;
    }

    void ConfigureProperties(ProjectRootElement project, BuildPlatformContext context, string? rid)
    {
        var player = context.Settings.Get<PlayerSettings>();
        var properties = project.AddPropertyGroup();
        properties.AddProperty("TargetFramework", TargetFramework);
        properties.AddProperty("OutputType", "Exe");
        properties.AddProperty("SupportedOSPlatformVersion", "33");
        properties.AddProperty("ApplicationId", player.ApplicationIdentifier);
        properties.AddProperty("ApplicationTitle", player.ProductName ?? context.Settings.Title ?? "Turian");
        properties.AddProperty("ApplicationVersion", context.Settings.Get<AndroidPlayerSettings>().VersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        properties.AddProperty("ApplicationDisplayVersion", player.Version ?? "1.0.0");
        properties.AddProperty("RuntimeIdentifiers", string.IsNullOrEmpty(rid) ? "android-arm64" : rid);
        properties.AddProperty("UseMonoRuntime", "false");
        properties.AddProperty("PublishAot", "false");
        properties.AddProperty("PublishTrimmed", "false");
        properties.AddProperty("EmbedAssembliesIntoApk", "true");
        ConfigureSigning(properties, context);
        properties.AddProperty("AndroidManifest", "AndroidManifest.xml");
        properties.AddProperty("AndroidStoreUncompressedFileExtensions",
            "$(AndroidStoreUncompressedFileExtensions);.oap;.json;.png");
        properties.AddProperty("_AndroidUseLibZipSharp", "true").Condition =
            "$([MSBuild]::IsOSPlatform('Windows'))";
        properties.AddProperty("ImplicitUsings", "disable");
        properties.AddProperty("Nullable", "enable");
        properties.AddProperty("AllowUnsafeBlocks", "true");
        properties.AddProperty("DefineConstants", "$(DefineConstants);$(TurianDefines)");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PublishArguments(BuildPlatformContext context)
    {
        List<string> arguments =
        [
            "publish", context.ProjectFile, "-o", context.OutputDirectory, "-c", context.Configuration,
            "--no-restore", "-m:4", "-nodeReuse:false",
            $"-p:TurianDefines={string.Join("%3B", context.Profile?.Symbols ?? [])}",
        ];

        // The tools found here, so the Android build does not depend on how the editor was started.
        if (Sdk is { } sdk)
        {
            arguments.Add($"-p:AndroidSdkDirectory={sdk}");
        }

        if (Jdk is { } jdk)
        {
            arguments.Add($"-p:JavaSdkDirectory={jdk}");
        }

        return arguments;
    }

    /// <summary>What is missing among the Android SDK and the JDK, or null when both are found.</summary>
    static string? MissingTools() =>
        (Sdk, Jdk) switch
        {
            (null, null) => $"{MissingSdk} {MissingJdk}",
            (null, _) => MissingSdk,
            (_, null) => MissingJdk,
            _ => null,
        };

    const string MissingSdk = "The Android SDK was not found: install it with Android Studio or the command-line "
                              + "tools, or set its folder in Settings, Build, Android.";

    const string MissingJdk = "No JDK 17 or newer was found: install one, or set its folder in Settings, Build, Android.";

    /// <inheritdoc />
    public BuildPlatformStatus CheckToolchain()
    {
        try
        {
            var dotnet = DotnetLocator.Find(requiredSdkMajor: 11);
            var packs = Path.Combine(Path.GetDirectoryName(dotnet)!, "packs");
            var installed = Directory.Exists(packs)
                && Directory.EnumerateDirectories(packs, "Microsoft.Android.Sdk.*")
                    .Any(static pack => Directory.EnumerateDirectories(pack)
                        .Any(static version => Path.GetFileName(version).StartsWith("37.", StringComparison.Ordinal)));
            if (!installed)
            {
                return new(false, "Install the .NET 11 Android workload: dotnet workload install android.");
            }

            return MissingTools() is { } missing
                ? new(false, missing)
                : new(true, $"Android SDK at {Sdk}, JDK at {Jdk}.");
        }
        catch (InvalidOperationException exception)
        {
            return new(false, exception.Message);
        }
    }

    /// <inheritdoc />
    public string? ValidateSettings(IAppSettings settings) =>
        ApplicationIdPattern().IsMatch(settings.Get<PlayerSettings>().ApplicationIdentifier ?? string.Empty)
            && settings.Get<AndroidPlayerSettings>().VersionCode > 0
            ? null : "Set Player Settings ApplicationIdentifier to a valid Android application id (for example org.example.app) and use a positive Android version code.";

    /// <inheritdoc />
    public Task PrepareAsync(BuildPlatformContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(context.Settings.CacheAbsoluteDir, "assetCatalog.json")))
        {
            throw new InvalidOperationException("Compile and import the project's assets before exporting Android.");
        }
        if (ReleaseSigningProblem(context) is { } signing)
        {
            throw new InvalidOperationException(signing);
        }
        if (MissingTools() is { } missing)
        {
            throw new InvalidOperationException(missing);
        }
        AndroidIcons.Write(context.Settings, Path.GetDirectoryName(context.ProjectFile)!);
        var assets = Path.Combine(Path.GetDirectoryName(context.ProjectFile)!, "AndroidAssets");
        var cacheRoot = Path.GetFullPath(context.Settings.CacheAbsoluteDir) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(assets).StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Android content staging must stay inside the project's cache.");
        }
        if (Directory.Exists(assets))
        {
            Directory.Delete(assets, recursive: true);
        }
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "android-player.json"),
            context.Settings.Get<AndroidPlayerSettings>().BackButtonQuits ? "true" : "false");
        var contentContext = context with { OutputDirectory = assets };
        BuildFinalizer.CopyProjectSettings(contentContext);
        BuildFinalizer.WriteTypeManifest(contentContext);
        BuildFinalizer.PackageAssets(contentContext);
        context.Logger.LogInformation("Android content prepared at {Path}", assets);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<BuildArtifact> FinalizeAsync(BuildPlatformContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packages = Directory.GetFiles(context.OutputDirectory, "*-Signed.apk");
        if (packages.Length == 0)
        {
            throw new InvalidDataException("Android publish produced no signed installable APK.");
        }
        foreach (var package in packages)
        {
            AndroidPackageValidator.Validate(package, context.Profile?.Development == true);
        }
        if (context.Profile?.Development != true
            && context.Settings.Get<AndroidPlayerSettings>().PackageFormat == AndroidPackageFormat.Aab
            && Directory.GetFiles(context.OutputDirectory, "*-Signed.aab").Length == 0)
        {
            throw new InvalidDataException("Android publish produced no signed App Bundle.");
        }
        return Task.FromResult(new BuildArtifact(context.OutputDirectory));
    }

    /// <inheritdoc />
    public Task<string> DeployAsync(BuildPlatformContext context, BuildArtifact artifact, string? device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(artifact);
        var adb = Adb();
        var package = Directory.GetFiles(artifact.OutputDirectory, "*-Signed.apk").SingleOrDefault()
            ?? throw new InvalidOperationException($"No single signed APK to install in {artifact.OutputDirectory}.");
        var application = context.Settings.Get<PlayerSettings>().ApplicationIdentifier!;
        return Task.Run(() =>
        {
            var serial = AndroidToolchain.ChooseDevice(AndroidToolchain.ListAllDevices(adb),
                string.IsNullOrWhiteSpace(device) ? Settings.Device : device);
            context.Logger.LogInformation("Installing {Package} on {Device}", Path.GetFileName(package), serial);
            AndroidToolchain.Install(adb, serial, package);
            if (AndroidToolchain.Start(adb, serial, application) is { } milliseconds)
            {
                context.Logger.LogInformation("Started {Application} on {Device} in {Milliseconds} ms", application,
                    serial, milliseconds);
            }
            else
            {
                context.Logger.LogInformation("Started {Application} on {Device}", application, serial);
            }
            return serial;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task FollowAsync(BuildPlatformContext context, string device, Action<string> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AndroidToolchain.FollowAsync(Adb(), device, context.Settings.Get<PlayerSettings>().ApplicationIdentifier!,
            write, cancellationToken);
    }

    static string Adb() => AndroidToolchain.FindAdb(Sdk) ?? throw new InvalidOperationException(
        Sdk is null ? MissingSdk : $"The Android SDK at {Sdk} has no platform tools: install them with the SDK manager.");

    void ReferenceRuntimeAssemblies(ProjectRootElement project, IBuildAppSettings settings,
        MASS4.Bricks.PackageManifest manifest)
    {
        var engine = settings.TurianPackages.Select(static assembly => assembly.Item2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = ProjectPackages.ResolveOrEmpty(settings.ProjectAbsoluteDir)
            .SelectMany(BrickAssemblies.RuntimeAssemblies).ToArray();
        var roots = new[] { BrickDirectory }.Concat(manifest.Dependencies.Keys
            .Select(static id => Path.Combine(ProjectPackages.BuiltinDirectory, id)));
        var libraries = roots.SelectMany(BrickAssemblies.PrecastDirectories)
            .Select(static path => Path.Combine(path, "lib")).ToArray();
        foreach (var (_, name) in Dependencies.Assemblies)
        {
            if (engine.Contains(name))
            {
                continue;
            }
            var path = installed.FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file),
                    name, StringComparison.OrdinalIgnoreCase))
                ?? libraries.Select(directory => Path.Combine(directory, $"{name}.dll")).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException($"Rebuild the Android platform: runtime assembly '{name}' is missing.");
            project.AddItemGroup().AddItem("Reference", name).AddMetadata("HintPath", path, true);
        }
    }

    static RuntimeDependencies LoadDependencies()
    {
        using var stream = typeof(AndroidBuildPlatform).Assembly
            .GetManifestResourceStream("Turian.Android.RuntimeDependencies")
            ?? throw new InvalidOperationException("The Android runtime dependency manifest is missing.");
        using var reader = new StreamReader(stream);
        return RuntimeDependencies.Parse(reader);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationIdPattern();
}
