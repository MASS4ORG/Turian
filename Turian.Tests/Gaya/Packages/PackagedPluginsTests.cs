namespace Turian.Tests;

/// <summary>Plugins installed into the application through studio-scope packages.</summary>
public sealed class PackagedPluginsTests : IDisposable
{
    static readonly Dictionary<string, SemanticVersion> Hosts = new() { ["gaya"] = SemanticVersion.Parse("1.0.0") };

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-studio-{Guid.NewGuid():N}");
    readonly string studio;

    /// <summary>Creates an empty studio folder.</summary>
    public PackagedPluginsTests()
    {
        studio = Path.Combine(root, "studio");
        Directory.CreateDirectory(studio);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A studio package's compiled plugin is loaded and discoverable by the host.</summary>
    [Fact]
    public void StudioPackagesContributePlugins()
    {
        var assemblyName = $"Acme.Theme{Guid.NewGuid():N}";
        Package("theme", "com.acme.theme", [PackageScope.Studio], assemblyName);
        Manifest(("com.acme.theme", "file:../../theme"));

        var loaded = Assert.Single(PackagedPlugins.Load(Hosts, NullLogger.Instance, studio));

        Assert.Equal(assemblyName, loaded.GetName().Name);
        Assert.Contains(loaded.GetTypes(), type => type.GetCustomAttribute<PluginAttribute>() is not null);
        Assert.True(File.Exists(Path.Combine(studio, "Bricks", LockFile.FileName)));
    }

    /// <summary>A package made for projects only is not loaded into the application.</summary>
    [Fact]
    public void ProjectOnlyPackagesAreRefused()
    {
        Package("tool", "com.acme.tool", [], $"Acme.Tool{Guid.NewGuid():N}");
        Manifest(("com.acme.tool", "file:../../tool"));

        Assert.Empty(PackagedPlugins.Load(Hosts, NullLogger.Instance, studio));
    }

    /// <summary>Without a studio manifest nothing is loaded.</summary>
    [Fact]
    public void NoManifestLoadsNothing() => Assert.Empty(PackagedPlugins.Load(Hosts, NullLogger.Instance, studio));

    void Package(string folder, string id, List<PackageScope> scopes, string assemblyName)
    {
        var path = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(path, PackagedPlugins.LibraryDirectoryName));
        new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0"), Scopes = scopes }.Save(path);

        var source = $$"""
            [Gaya.Sdk.Plugin("{{id}}", "Acme Theme")]
            public sealed class ThemePlugin : Gaya.Sdk.IPlugin
            {
                public void Configure(Gaya.Sdk.IPluginContext context) { }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static p => MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IPlugin).Assembly.Location));
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(Path.Combine(path, PackagedPlugins.LibraryDirectoryName, $"{assemblyName}.dll"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }

    void Manifest(params (string Id, string Spec)[] dependencies) =>
        new ProjectManifest { Dependencies = dependencies.ToDictionary(static d => d.Id, static d => (string?)d.Spec) }
            .Save(studio);
}
