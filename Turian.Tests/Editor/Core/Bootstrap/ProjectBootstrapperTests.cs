namespace Turian.Tests;

/// <summary>Verifies the scaffold a new project starts from.</summary>
public sealed class ProjectBootstrapperTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-bootstrap-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>The project folder is named after the title and holds the starter files.</summary>
    [Fact]
    public async Task CreatesStarterFiles()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "My Game"));

        Assert.NotNull(project);
        var assets = Path.Combine(project, "Assets");
        Assert.True(File.Exists(Path.Combine(assets, "scene-01.prefab")));
        Assert.True(File.Exists(Path.Combine(assets, "Game.cs")));
    }

    /// <summary>Scripts see the engine, its attributes and the key codes without their own usings.</summary>
    [Fact]
    public async Task WritesGlobalUsings()
    {
        var project = await new ProjectBootstrapper().CreateAsync(Path.Combine(root, "Game"));

        var globals = await File.ReadAllTextAsync(
            Path.Combine(project!, "Assets", "Globals.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("global using Turian.Engine.Core;", globals, StringComparison.Ordinal);
        Assert.Contains("global using Turian;", globals, StringComparison.Ordinal);
        Assert.Contains("global using Silk.NET.Input;", globals, StringComparison.Ordinal);
    }
}
