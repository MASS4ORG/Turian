namespace Turian.Tests;

/// <summary>
/// Verifies that every script declaring a class gets a stable TypeId, which is what lets scenes save
/// and load the components declared in it.
/// </summary>
public sealed class UserCodeTypeManifestGeneratorTests : IDisposable
{
    readonly string projectDirectory = Path.Combine(Path.GetTempPath(), $"turian-manifest-{Guid.NewGuid():N}");
    readonly string assetsDirectory;

    /// <summary>Creates an empty project with an Assets folder.</summary>
    public UserCodeTypeManifestGeneratorTests()
    {
        assetsDirectory = Path.Combine(projectDirectory, "Assets");
        Directory.CreateDirectory(assetsDirectory);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(projectDirectory, recursive: true);

    /// <summary>A new script without a meta file gets one, and its id becomes the TypeId.</summary>
    [Fact]
    public void ScriptWithoutMetaGetsOneWithItsTypeId()
    {
        var script = Path.Combine(assetsDirectory, "Spinner.cs");
        File.WriteAllText(script, "namespace Usercode;\npublic class Spinner : Component {}\n");

        var manifest = UserCodeTypeManifestGenerator.Generate(assetsDirectory, NullLogger.Instance);

        var entry = Assert.Single(manifest.Types);
        Assert.Equal("Usercode.Spinner", entry.FullyQualifiedName);
        Assert.True(File.Exists(script + ".meta"));
        using var meta = JsonDocument.Parse(File.ReadAllText(script + ".meta"));
        Assert.Equal(entry.TypeId, meta.RootElement.GetProperty("Id").GetGuid());
        Assert.Equal("Assets/Spinner.cs", meta.RootElement.GetProperty("RelativePath").GetString());
    }

    /// <summary>The id is created once: later generations reuse it, so saved scenes keep resolving.</summary>
    [Fact]
    public void TypeIdIsStableAcrossGenerations()
    {
        File.WriteAllText(Path.Combine(assetsDirectory, "Spinner.cs"), "namespace Usercode;\npublic class Spinner {}\n");

        var first = UserCodeTypeManifestGenerator.Generate(assetsDirectory, NullLogger.Instance);
        var second = UserCodeTypeManifestGenerator.Generate(assetsDirectory, NullLogger.Instance);

        Assert.Equal(Assert.Single(first.Types).TypeId, Assert.Single(second.Types).TypeId);
    }

    /// <summary>A file without a class, such as one holding only global usings, gets no meta.</summary>
    [Fact]
    public void FileWithoutClassGetsNoMeta()
    {
        var globals = Path.Combine(assetsDirectory, "Globals.cs");
        File.WriteAllText(globals, "global using System;\n");

        var manifest = UserCodeTypeManifestGenerator.Generate(assetsDirectory, NullLogger.Instance);

        Assert.Empty(manifest.Types);
        Assert.False(File.Exists(globals + ".meta"));
    }

    /// <summary>The assembly name is recorded so a single-file game can load the assembly before resolving types.</summary>
    [Fact]
    public void AssemblyNameIsRecorded()
    {
        var manifest = UserCodeTypeManifestGenerator.Generate(assetsDirectory, NullLogger.Instance, "My Game");

        Assert.Equal("My Game", manifest.AssemblyName);
    }
}
