namespace Turian.Tests;

/// <summary>
/// Covers the mechanism behind play mode state revert: the Studio plays a deep copy
/// of the scene being edited, so the edited hierarchy is never mutated and stopping play needs no
/// restore step.
/// </summary>
public class PlayModeIsolationTests : IDisposable
{
    readonly AssetDatabase assetDatabase;

    /// <summary>Resets the <see cref="AssetDatabase"/> singleton, as the other suites do.</summary>
    public PlayModeIsolationTests()
    {
        TestAssetDatabase.Reset();
        assetDatabase = new AssetDatabase();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        GC.SuppressFinalize(this);
    }

    [TypeId("a4000002-0000-4000-8000-000000000001")]
    sealed class DriftingComponent : Component
    {
        /// <summary>Moves its node every frame, standing in for arbitrary gameplay code.</summary>
        public override void OnUpdate(float deltaTime)
        {
            var node = Node ?? throw new InvalidOperationException("DriftingComponent must be attached to a node.");
            node.Transform.Position += new Vector3(1f, 0f, 0f);
        }
    }

    static Node BuildScene()
    {
        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Mover" };
        child.AddComponent(new DriftingComponent());
        root.Children.Add(child);
        root.Awake(null);
        return root;
    }

    /// <summary>
    /// Running a session against a deep copy leaves the edited scene byte-identical to its
    /// pre-play serialization, while the copy itself advances.
    /// </summary>
    [Fact]
    public void PlayingACopy_LeavesTheEditedSceneUntouched()
    {
        var editorRoot = BuildScene();
        var before = Serializer.Serialize(editorRoot);

        var playRoot = NodeCloner.DeepClone(editorRoot);
        Assert.NotNull(playRoot);

        var sceneManager = new SceneManager(assetDatabase);
        sceneManager.AdoptScene(Guid.NewGuid(), playRoot);
        var ticker = new SceneTicker(sceneManager);

        ticker.Tick(0.016);
        ticker.Tick(0.016);

        Assert.Equal(before, Serializer.Serialize(editorRoot));
        Assert.NotEqual(before, Serializer.Serialize(playRoot));
        Assert.Equal(2f, playRoot.Children[0].Transform.Position.X);
        Assert.Equal(0f, editorRoot.Children[0].Transform.Position.X);
    }

    /// <summary>
    /// The copy owns its own components, so a mutation on one side cannot reach the other. This is
    /// the reason play mode uses <see cref="NodeCloner.DeepClone"/> rather than
    /// <see cref="NodeCloner.Clone"/>, which shares the source's component instances.
    /// </summary>
    [Fact]
    public void DeepClone_DoesNotShareComponentInstances()
    {
        var editorRoot = BuildScene();

        var clone = NodeCloner.DeepClone(editorRoot);

        Assert.NotNull(clone);
        Assert.NotSame(
            editorRoot.Children[0].Components[0],
            clone.Children[0].Components[0]);
    }

    /// <summary>An adopted hierarchy is tracked as the active scene without touching the disk.</summary>
    [Fact]
    public void AdoptScene_TracksTheHierarchyAsTheActiveScene()
    {
        var sceneManager = new SceneManager(assetDatabase);
        var root = BuildScene();
        var assetId = Guid.NewGuid();

        var scene = sceneManager.AdoptScene(assetId, root);

        Assert.Same(root, scene.RootNode);
        Assert.Same(scene, sceneManager.ActiveScene);
        Assert.True(sceneManager.TryGetLoadedScene(assetId, out _));
    }

    /// <summary>A single-mode adopt replaces whatever was loaded before.</summary>
    [Fact]
    public void AdoptScene_SingleModeUnloadsPreviousScenes()
    {
        var sceneManager = new SceneManager(assetDatabase);
        sceneManager.AdoptScene(Guid.NewGuid(), BuildScene());

        sceneManager.AdoptScene(Guid.NewGuid(), BuildScene());

        Assert.Single(sceneManager.LoadedScenes);
    }
}
