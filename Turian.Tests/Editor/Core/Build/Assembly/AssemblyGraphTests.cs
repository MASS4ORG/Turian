namespace Turian.Tests;

/// <summary>
/// Verifies how assembly definitions split a project's scripts into assemblies, and which graphs a build
/// refuses.
/// </summary>
public sealed class AssemblyGraphTests : IDisposable
{
    const string DefaultName = "Game";

    readonly string projectDirectory = Path.Combine(Path.GetTempPath(), $"turian-asmdef-{Guid.NewGuid():N}");
    readonly string assetsDirectory;

    /// <summary>Creates an empty project with an Assets folder.</summary>
    public AssemblyGraphTests()
    {
        assetsDirectory = Path.Combine(projectDirectory, "Assets");
        Directory.CreateDirectory(assetsDirectory);
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(projectDirectory, recursive: true);

    /// <summary>Without definitions every script compiles into the default assembly.</summary>
    [Fact]
    public void WithoutDefinitionsEverythingIsDefault()
    {
        var graph = AssemblyGraph.Discover(assetsDirectory, DefaultName);

        Assert.Empty(graph.Definitions);
        Assert.Empty(graph.Default.References);
        Assert.Same(graph.Default, graph.AssemblyFor(Path.Combine(assetsDirectory, "scripts", "Spinner.cs")));
    }

    /// <summary>A script belongs to the definition in its closest folder, nested ones included.</summary>
    [Fact]
    public void ScriptsBelongToTheClosestDefinition()
    {
        Define("Inventory", "Acme.Inventory");
        Define(Path.Combine("Inventory", "Editor"), "Acme.Inventory.Editor", editorOnly: true);

        var graph = AssemblyGraph.Discover(assetsDirectory, DefaultName);

        Assert.Equal("Acme.Inventory", graph.AssemblyFor(Script("Inventory", "Bag.cs")).Name);
        Assert.Equal("Acme.Inventory.Editor", graph.AssemblyFor(Script("Inventory", "Editor", "Menu.cs")).Name);
        Assert.Equal(DefaultName, graph.AssemblyFor(Script("Player.cs")).Name);

        var inventory = graph.Definitions.Single(a => a.Name == "Acme.Inventory");
        Assert.Equal([Path.Combine(assetsDirectory, "Inventory", "Editor")], graph.ExcludedDirectories(inventory));
        Assert.Equal(2, graph.ExcludedDirectories(graph.Default).Count());
    }

    /// <summary>The default assembly references what ships and is auto-referenced, nothing editor-only.</summary>
    [Fact]
    public void DefaultReferencesShippedAutoReferencedDefinitions()
    {
        Define("Inventory", "Acme.Inventory");
        Define("Tools", "Acme.Tools", editorOnly: true);
        Define("Internal", "Acme.Internal", autoReferenced: false);

        var graph = AssemblyGraph.Discover(assetsDirectory, DefaultName);

        Assert.Equal(["Acme.Inventory"], graph.Default.References);
    }

    /// <summary>A definition comes after every definition it references.</summary>
    [Fact]
    public void DefinitionsAreOrderedAfterTheirReferences()
    {
        var core = Define("Core", "Z.Core");
        var items = Define("Items", "A.Items", references: [core]);
        Define("Shop", "M.Shop", references: [items, core]);

        var names = AssemblyGraph.Discover(assetsDirectory, DefaultName).Definitions.Select(a => a.Name).ToList();

        Assert.True(names.IndexOf("Z.Core") < names.IndexOf("A.Items"));
        Assert.True(names.IndexOf("A.Items") < names.IndexOf("M.Shop"));
    }

    /// <summary>An empty name takes the file name, and so does the root namespace.</summary>
    [Fact]
    public void EmptyNameUsesTheFileName()
    {
        Define("Inventory", name: null, fileName: "Acme.Bags");

        var assembly = Assert.Single(AssemblyGraph.Discover(assetsDirectory, DefaultName).Definitions);

        Assert.Equal("Acme.Bags", assembly.Name);
        Assert.Equal("Acme.Bags", assembly.RootNamespace);
    }

    /// <summary>Two definitions with one name are refused.</summary>
    [Fact]
    public void DuplicateNamesAreRefused()
    {
        Define("A", "Acme.Same");
        Define("B", "Acme.Same");

        Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
    }

    /// <summary>A definition may not take the default assembly's name.</summary>
    [Fact]
    public void TheDefaultNameIsRefused()
    {
        Define("A", DefaultName);

        Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
    }

    /// <summary>A folder holds at most one definition.</summary>
    [Fact]
    public void TwoDefinitionsInOneFolderAreRefused()
    {
        Define("A", "Acme.One", fileName: "One");
        Define("A", "Acme.Two", fileName: "Two");

        Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
    }

    /// <summary>References that loop back are refused.</summary>
    [Fact]
    public void CyclesAreRefused()
    {
        var aId = Guid.NewGuid();
        var bId = Define("B", "Acme.B", references: [aId]);
        Define("A", "Acme.A", references: [bId], id: aId);

        var error = Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Code that ships may not depend on code that does not.</summary>
    [Fact]
    public void ShippedCodeReferencingEditorOnlyIsRefused()
    {
        var tools = Define("Tools", "Acme.Tools", editorOnly: true);
        Define("Game", "Acme.Game", references: [tools]);

        Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
    }

    /// <summary>A reference to something that is not a definition is refused.</summary>
    [Fact]
    public void UnknownReferencesAreRefused()
    {
        Define("A", "Acme.A", references: [Guid.NewGuid()]);

        Assert.Throws<InvalidOperationException>(() => AssemblyGraph.Discover(assetsDirectory, DefaultName));
    }

    /// <summary>Only data assets holding a definition are read as one.</summary>
    [Fact]
    public void DefinitionFilesAreRecognizedWithoutLoading()
    {
        Define("A", "Acme.A", fileName: "Acme.A");
        var other = Path.Combine(assetsDirectory, "Other.dataasset");
        File.WriteAllText(other, """{ "__TypeId": "a3000000-0000-4000-8000-000000000004", "Id": "00000000-0000-4000-8000-000000000001" }""");

        Assert.True(AssemblyGraph.IsDefinitionFile(Path.Combine(assetsDirectory, "A", "Acme.A.dataasset")));
        Assert.False(AssemblyGraph.IsDefinitionFile(other));
    }

    /// <summary>Each definition gets a project of its own, referenced by the default assembly's project.</summary>
    [Fact]
    public void EachDefinitionGetsAProject()
    {
        Define("Inventory", "Acme.Inventory");
        var settings = Substitute.For<IBuildAppSettings>();
        settings.TitleToPathFriendly.Returns(DefaultName);
        settings.AssetsAbsoluteDir.Returns(assetsDirectory);
        settings.CacheAbsoluteDir.Returns(Path.Combine(projectDirectory, ".Cache"));
        settings.CacheSourceRelativeDir.Returns("../../Assets");
        settings.PackageReferences.Returns([]);
        settings.TurianPackages.Returns([]);

        var projects = CsProjectGenerator.GenerateUserCodeProjects(settings, NullLogger.Instance,
            CsProjectGenerator.DiscoverAssemblies(settings));

        Assert.Equal(2, projects.Count);
        var inventory = projects[0];
        Assert.Equal(Path.Combine(projectDirectory, ".Cache", "Assemblies", "Acme.Inventory", "Acme.Inventory.csproj"),
            inventory.FullPath);
        Assert.Contains(inventory.Properties, p => p is { Name: "AssemblyName", Value: "Acme.Inventory" });
        Assert.Contains(inventory.Items, i => i.ItemType == "Compile" && i.Include == "../../../Assets/Inventory/**/*.cs");

        var game = projects[1];
        Assert.Contains(game.Items, i => i.ItemType == "ProjectReference"
                                        && i.Include == "../Assemblies/Acme.Inventory/Acme.Inventory.csproj");
        Assert.Contains(game.Items, i => i.ItemType == "Compile" && i.Exclude.Contains("../../Assets/Inventory/**"));
    }

    string Script(params string[] parts) => Path.Combine([assetsDirectory, .. parts]);

    Guid Define(
        string folder,
        string? name,
        bool editorOnly = false,
        bool autoReferenced = true,
        Guid[]? references = null,
        string? fileName = null,
        Guid? id = null)
    {
        var assetId = id ?? Guid.NewGuid();
        var directory = Path.Combine(assetsDirectory, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{fileName ?? name ?? "Definition"}.dataasset");

        var json = new JsonObject
        {
            ["__TypeId"] = AssemblyDefinition.TypeIdValue,
            ["Name"] = name,
            ["EditorOnly"] = editorOnly,
            ["AutoReferenced"] = autoReferenced,
            ["References"] = new JsonArray([.. (references ?? []).Select(r => (JsonNode)new JsonObject { ["AssetId"] = r })]),
            ["Id"] = assetId,
        };
        File.WriteAllText(path, json.ToJsonString());
        File.WriteAllText($"{path}.meta",
            $$"""{ "__TypeId": "a3000000-0000-4000-8000-000000000006", "RelativePath": "{{Path.GetRelativePath(projectDirectory, path)}}", "Id": "{{assetId}}" }""");

        return assetId;
    }
}
