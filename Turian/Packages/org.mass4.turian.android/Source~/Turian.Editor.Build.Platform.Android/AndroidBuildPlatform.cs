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

    /// <inheritdoc />
    public IReadOnlyList<BuildDevice>? Devices => [];

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
    public IReadOnlyList<string> PublishArguments(BuildPlatformContext context) =>
        ["publish", context.ProjectFile, "-o", context.OutputDirectory, "-c", context.Configuration,
            "--no-restore", "-m:4", "-nodeReuse:false",
            $"-p:TurianDefines={string.Join("%3B", context.Profile?.Symbols ?? [])}"];

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
            return installed
                ? new(true, "The .NET 11 Android SDK pack is installed; export validates the Android SDK and JDK.")
                : new(false, "Install the .NET 11 Android workload, Android SDK platform 36 and a compatible JDK.");
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
