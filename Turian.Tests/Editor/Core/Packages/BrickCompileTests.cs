using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>
/// Packing a brick compiles its sources through the command line. Each test owns its scratch folder and the command
/// line gets its own brick store, so nothing here touches state other tests share.
/// </summary>
[Trait("Category", "E2E")]
public sealed class BrickCompileTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-brick-compile-{Guid.NewGuid():N}");

    /// <inheritdoc/>
    public void Dispose() => PrecastBrickFixture.DeleteTree(root);

    /// <summary>A failed source compilation is reported and cannot leave a publishable archive behind.</summary>
    [Fact]
    public async Task BrokenSourcesCannotProduceAnArchive()
    {
        var folder = BrickService.New(root, "user.mateo.broken");
        var (initialCode, initialOutput) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder,
            "--precast", "--out", Path.Combine(root, "initial-output"));
        Assert.True(initialCode == 0, initialOutput);
        Assert.True(Directory.Exists(Path.Combine(folder, "Brick~", "Precast")));
        File.WriteAllText(Path.Combine(folder, "Runtime", "BrokenComponent.cs"),
            "namespace Mateo.Broken; public class BrokenComponent : MissingBaseType { }");
        var outputDirectory = Path.Combine(root, "failed-output");
        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder,
            "--precast", "--out", outputDirectory);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("does not compile", output, StringComparison.Ordinal);
        Assert.True(!Directory.Exists(outputDirectory) || !Directory.EnumerateFiles(outputDirectory, "*.brick").Any());
        Assert.False(Directory.Exists(Path.Combine(folder, "Brick~", "Precast")));
    }

    /// <summary>Consumer-built editor assemblies can use the editor import contract without an engine checkout.</summary>
    [Fact]
    public async Task EditorSourcesCompileAgainstTheEditorContract()
    {
        var folder = BrickService.New(root, "user.mateo.importer");
        var manifest = PackageManifest.Load(folder, ["turian"]);
        manifest.Roles.Remove(BrickLayout.Precast);
        manifest.Save(folder);
        var definitionPath = Path.Combine(folder, "Runtime", "Mateo.Importer.dataasset");
        var definition = Serializer.Load<AssemblyDefinition>(definitionPath)!;
        definition.EditorOnly = true;
        definition.EmbeddedResources.Add("Resources~/notes.txt");
        Serializer.Save(definitionPath, definition);
        Assert.Contains("Resources~/notes.txt", File.ReadAllText(definitionPath), StringComparison.Ordinal);
        Assert.Equal(["Resources~/notes.txt"], Serializer.Load<AssemblyDefinition>(definitionPath)!.EmbeddedResources);
        Directory.CreateDirectory(Path.Combine(folder, "Runtime", "Resources~"));
        File.WriteAllText(Path.Combine(folder, "Runtime", "Resources~", "notes.txt"), "Native payload stays embedded.");
        Assert.Equal(["Resources~/notes.txt"], AssemblyGraph.Discover(folder, "Game").Definitions.Single().EmbeddedResources);
        File.WriteAllText(Path.Combine(folder, "Runtime", "ImporterComponent.cs"),
            "namespace Mateo.Importer; public class ImporterComponent { public System.Type Contract => typeof(Turian.Editor.Core.IAssetImporter); }");

        var (exitCode, output) = await PrecastBrickFixture.RunCli(root, "brick", "pack", folder, "--precast", "--twin", "--out", Path.Combine(root, "compiled"));

        Assert.True(exitCode == 0, output);
        var assemblyPath = Path.Combine(folder, "Brick~", "Precast", "editor", "Mateo.Importer.dll");
        Assert.True(File.Exists(assemblyPath));
        var context = new AssemblyLoadContext("brick-resource", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            Assert.Contains("Mateo.Importer.Resources~.notes.txt", assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream("Mateo.Importer.Resources~.notes.txt")!;
            using var reader = new StreamReader(stream);
            Assert.Equal("Native payload stays embedded.", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            context.Unload();
        }
    }
}
