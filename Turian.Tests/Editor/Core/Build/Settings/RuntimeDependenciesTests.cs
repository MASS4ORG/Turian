namespace Turian.Tests.Turian.Editor.Build;

/// <summary>Checks the dependency snapshot consumed by generated game projects.</summary>
public class RuntimeDependenciesTests
{
    /// <summary>Resolved versions, native packages and copied project names survive the manifest.</summary>
    [Fact]
    public void ParsesResolvedDependenciesAndNormalizesProjectPaths()
    {
        using var reader = new StringReader("""
            framework|net11.0
            package|Graphics.Native|3.4.5
            package|Graphics.Api|2.3.4
            assembly|Engine\Core|Engine.Core|Engine\Core\Core.csproj
            assembly|Launcher|Attributes|
            """);

        var result = RuntimeDependencies.Parse(reader);

        Assert.Equal("net11.0", result.TargetFramework);
        Assert.Equal([("Graphics.Api", "2.3.4"), ("Graphics.Native", "3.4.5")], result.Packages);
        Assert.Equal([("Launcher", "Attributes"), ("Engine/Core", "Engine.Core")], result.Assemblies);
        Assert.Equal(["Engine/Core/Core"], result.Projects);
    }

    /// <summary>Malformed entries cannot silently produce an incomplete game dependency list.</summary>
    [Theory]
    [InlineData("unknown|Package|1.0")]
    [InlineData("package|Package")]
    [InlineData("assembly|Engine|Engine.Core")]
    [InlineData("framework|net10.0|extra")]
    public void RejectsInvalidEntries(string entry)
    {
        using var reader = new StringReader(entry);
        Assert.Throws<InvalidDataException>(() => RuntimeDependencies.Parse(reader));
    }

    /// <summary>The generated snapshot must contain framework, package and assembly information.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("package|Package|1.0\nassembly|Engine|Engine.Core|")]
    [InlineData("framework|net10.0\nassembly|Engine|Engine.Core|")]
    [InlineData("framework|net10.0\npackage|Package|1.0")]
    public void RejectsIncompleteManifests(string manifest)
    {
        using var reader = new StringReader(manifest);
        Assert.Throws<InvalidDataException>(() => RuntimeDependencies.Parse(reader));
    }

    /// <summary>Caller edits cannot change dependencies used by another compilation or export.</summary>
    [Fact]
    public void SettingsReturnIndependentSnapshots()
    {
        var settings = new BuildAppSettings();
        var packages = settings.PackageReferences;
        var assemblies = settings.TurianPackages;
        var projects = settings.InternalPackages;
        var names = settings.Packages;
        packages[0] = ("Changed", "0");
        assemblies[0] = ("Changed", "Changed");
        projects[0] = "Changed";
        names[0] = "Changed";

        Assert.DoesNotContain(settings.PackageReferences, static package => package.Item1 == "Changed");
        Assert.DoesNotContain(settings.TurianPackages, static assembly => assembly.Item2 == "Changed");
        Assert.DoesNotContain("Changed", settings.InternalPackages);
        Assert.DoesNotContain("Changed", settings.Packages);
    }

    /// <summary>The compiled snapshot follows the restored launcher graph and excludes editor-only dependencies.</summary>
    [Fact]
    public void CompiledManifestMatchesRuntimeAssemblies()
    {
        var settings = new BuildAppSettings();
        var packages = settings.PackageReferences.ToDictionary(static package => package.Item1,
            static package => package.Item2, StringComparer.OrdinalIgnoreCase);
        using var assets = typeof(RuntimeDependenciesTests).Assembly
            .GetManifestResourceStream("Turian.Tests.RuntimeLauncherAssets")!;
        using var document = JsonDocument.Parse(assets);
        var resolved = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(static item => item.Value.GetProperty("type").GetString() == "package")
            .ToDictionary(static item => item.Name[..item.Name.LastIndexOf('/')],
                static item => item.Name[(item.Name.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(resolved["Microsoft.Extensions.Hosting"], packages["Microsoft.Extensions.Hosting"]);
        Assert.All(settings.PackageReferences, package => Assert.Equal(resolved[package.Item1], package.Item2));
        Assert.Contains("Microsoft.Extensions.DependencyInjection.Abstractions", packages.Keys);
        Assert.Contains("Ultz.Native.GLFW", packages.Keys);
        Assert.DoesNotContain(packages.Keys, static name => name.StartsWith("Silk.NET.Assimp", StringComparison.Ordinal));
        Assert.DoesNotContain(packages.Keys, static name => name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
        Assert.DoesNotContain("System.Composition", packages.Keys);
        Assert.DoesNotContain("JetBrains.Annotations", packages.Keys);
        Assert.Contains(settings.TurianPackages, static assembly => assembly.Item2 == "Gaya.Attributes");
        Assert.All(settings.TurianPackages, static assembly => Assert.False(Path.IsPathRooted(assembly.Item1)));
        Assert.Equal(settings.PackageReferences.Select(static package => package.Item1), settings.Packages);
    }
}
