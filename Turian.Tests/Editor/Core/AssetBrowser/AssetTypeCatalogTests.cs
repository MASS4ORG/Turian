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
