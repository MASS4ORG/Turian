namespace Turian.Tests.Editor;

/// <summary>
/// Covers what opening an asset from the browser does for each kind: scenes open as documents,
/// authored assets are edited in the inspector, source files go to an external program, and anything
/// unclaimed does nothing.
/// </summary>
public class AssetTypeCatalogTests
{
    readonly AssetTypeCatalog catalog = new();

    /// <summary>Each built-in kind opens the way its file is meant to be edited.</summary>
    [Theory]
    [InlineData("Assets/Level.prefab", AssetActivation.Edit)]
    [InlineData("Assets/Brick.material", AssetActivation.Inspect)]
    [InlineData("Assets/PuzzleData.dataasset", AssetActivation.Inspect)]
    [InlineData("Assets/Wall.png", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Bottle.glb", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Player.cs", AssetActivation.ExternalProgram)]
    [InlineData("Assets/LICENSE.txt", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Menu.ui", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Menu.pss", AssetActivation.ExternalProgram)]
    [InlineData("Assets/README.md", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Table.csv", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Menu.uss", AssetActivation.None)]
    [InlineData("Assets/theme.mp3", AssetActivation.None)]
    [InlineData("Assets/no-extension", AssetActivation.None)]
    public void BuiltInKindsOpenTheWayTheirFilesAreEdited(string path, AssetActivation expected) =>
        Assert.Equal(expected, catalog.ActivationFor(path));

    /// <summary>Extensions match regardless of case, as file systems on Windows and macOS do.</summary>
    [Fact]
    public void ExtensionsMatchRegardlessOfCase() =>
        Assert.Equal("turian.texture", catalog.Resolve("Assets/WALL.PNG")?.Id);

    /// <summary>A kind registered later takes over the extensions it claims.</summary>
    [Fact]
    public void ALaterRegistrationTakesOverAnExtension()
    {
        catalog.Register(new AssetTypeDescriptor("plugin.image", "Image", [".png"], AssetActivation.Edit));

        Assert.Equal("plugin.image", catalog.Resolve("Assets/Wall.png")?.Id);
        Assert.Equal(AssetActivation.ExternalProgram, catalog.ActivationFor("Assets/Wall.jpg"));
    }

    /// <summary>
    /// A file a document kind claims opens as a document while the kind is registered, whatever its asset kind says,
    /// and returns to that answer afterwards.
    /// </summary>
    [Theory]
    [InlineData("Assets/Menu.ui", AssetActivation.ExternalProgram)]
    [InlineData("Assets/Menu.fake", AssetActivation.None)]
    public void AClaimedFileOpensAsADocumentWhileItsKindIsRegistered(string path, AssetActivation otherwise)
    {
        var kinds = new WorkspaceDocumentKindRegistry();
        var claiming = new AssetTypeCatalog([], kinds);
        Assert.Equal(otherwise, claiming.ActivationFor(path));

        var registration = kinds.Add(new FakeDocumentKind { Extension = Path.GetExtension(path) });

        Assert.Equal(AssetActivation.Edit, claiming.ActivationFor(path));
        Assert.Equal(AssetActivation.ExternalProgram, claiming.ActivationFor("Assets/Menu.pss"));
        Assert.Equal(AssetActivation.None, claiming.ActivationFor(null));
        Assert.Equal(AssetActivation.None, claiming.ActivationFor(" "));

        registration.Dispose();

        Assert.Equal(otherwise, claiming.ActivationFor(path));
    }

    /// <summary>A kind that cannot check a file does not claim it.</summary>
    [Fact]
    public void AKindThatCannotCheckAFileDoesNotClaimIt()
    {
        var kinds = new WorkspaceDocumentKindRegistry([new FakeDocumentKind
        {
            CheckFailure = new InvalidOperationException(),
        }]);

        Assert.Equal(AssetActivation.ExternalProgram,
            new AssetTypeCatalog([], kinds).ActivationFor("Assets/notes.txt"));
        Assert.Null(kinds.KindFor("Assets/notes.txt"));
    }

    /// <summary>The editor's own catalog and workspace read one registry.</summary>
    [Fact]
    public void TheEditorServicesShareOneRegistryOfDocumentKinds()
    {
        using var services = new ServiceCollection().AddEditorServices(NullLogger.Instance).BuildServiceProvider();
        var types = services.GetRequiredService<AssetTypeCatalog>();
        using var registration = services.GetRequiredService<WorkspaceDocumentKindRegistry>()
            .Add(new FakeDocumentKind());

        Assert.Equal(AssetActivation.Edit, types.ActivationFor("Assets/Menu.fake"));
    }

    /// <summary>The scene's asset kind and its document kind are one id.</summary>
    [Fact]
    public void ScenesHaveOneIdForTheirAssetAndDocumentKind() =>
        Assert.Equal(WorkspaceDocumentKinds.Scene, catalog.Resolve("Assets/Level.prefab")!.Id);

    /// <summary>Each built-in kind carries the fixed id its shared <see cref="AssetTypeIds"/> constant names.</summary>
    [Theory]
    [InlineData(".prefab", "turian.scene")]
    [InlineData(".material", "turian.material")]
    [InlineData(".dataasset", "turian.dataAsset")]
    [InlineData(".png", "turian.texture")]
    [InlineData(".fbx", "turian.model")]
    [InlineData(".cs", "turian.script")]
    [InlineData(".txt", "turian.text")]
    [InlineData(".ui", "turian.uiDocument")]
    [InlineData(".pss", "turian.uiStyleSheet")]
    public void BuiltInKindsCarryTheirFixedIds(string extension, string id) =>
        Assert.Equal(id, catalog.Resolve("Assets/file" + extension)!.Id);

    /// <summary>
    /// The catalog and the workspace document kinds answer one question together: a file the kinds claim is edited as a
    /// document, and every other file is not, whatever its asset kind would do.
    /// </summary>
    [Theory]
    [InlineData("Assets/Menu.ui")]
    [InlineData("Assets/Menu.fake")]
    [InlineData("Assets/Menu.pss")]
    [InlineData("Assets/notes.txt")]
    public void ActivationForEditsExactlyWhereAKindClaims(string path)
    {
        var kinds = new WorkspaceDocumentKindRegistry();
        using var ui = kinds.Add(new FakeDocumentKind { Id = "test.ui", Extension = ".ui" });
        using var fake = kinds.Add(new FakeDocumentKind { Id = "test.fake", Extension = ".fake" });
        var registered = new AssetTypeCatalog([], kinds);
        var claimed = kinds.KindFor(path) is not null;

        Assert.Equal(claimed, registered.ActivationFor(path) == AssetActivation.Edit);
        Assert.Equal(claimed ? AssetActivation.Edit : registered.Resolve(path)?.Activation ?? AssetActivation.None,
            registered.ActivationFor(path));
    }

    /// <summary>Re-registering an id replaces the kind rather than listing it twice.</summary>
    [Fact]
    public void ReRegisteringAnIdReplacesTheKind()
    {
        var before = catalog.Types.Count;

        catalog.Register(new AssetTypeDescriptor("turian.script", "Script", [".cs"], AssetActivation.None));

        Assert.Equal(before, catalog.Types.Count);
        Assert.Equal(AssetActivation.None, catalog.ActivationFor("Assets/Player.cs"));
    }
}
