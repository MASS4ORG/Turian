namespace Turian.Tests;

/// <summary>
/// Tests for the SceneManager class.
/// </summary>
public class SceneManagerTests
{
    readonly SceneManager sceneManager;

    /// <summary>
    /// Initializes a new instance of the SceneManagerTests class.
    /// </summary>
    public SceneManagerTests()
    {
        ResetAssetDatabase();
        var assetDatabase = new AssetDatabase();
        sceneManager = new SceneManager(assetDatabase);
    }

    void ResetAssetDatabase()
    {
        var field = typeof(AssetDatabase).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(null, null);
        }
    }

    /// <summary>
    /// Verifies that the constructor correctly initializes the PersistentRoot node.
    /// </summary>
    [Fact]
    public void Constructor_InitializesPersistentRoot()
    {
        Assert.NotNull(sceneManager.PersistentRoot);
        Assert.Equal("PersistentRoot", sceneManager.PersistentRoot.Name);
    }

    /// <summary>
    /// Verifies that ActiveScene can be set and retrieved correctly.
    /// </summary>
    [Fact]
    public void ActiveScene_SetAndGet()
    {
        var node = new Node { Name = "SceneRoot" };
        var scene = new LoadedScene(Guid.NewGuid(), node);

        var field = typeof(SceneManager).GetField("loadedScenes", BindingFlags.NonPublic | BindingFlags.Instance);
        var loadedScenes = (List<LoadedScene>?)field?.GetValue(sceneManager);
        loadedScenes?.Add(scene);

        sceneManager.ActiveScene = scene;
        Assert.Equal(scene, sceneManager.ActiveScene);
    }

    /// <summary>
    /// Verifies that setting ActiveScene to an untracked scene throws an exception.
    /// </summary>
    [Fact]
    public void ActiveScene_ThrowsIfNotTracked()
    {
        var node = new Node { Name = "SceneRoot" };
        var scene = new LoadedScene(Guid.NewGuid(), node);

        Assert.Throws<InvalidOperationException>(() => sceneManager.ActiveScene = scene);
    }
}
