namespace Turian.Tests;

/// <summary>Tests for prefab editing mode's way back to the scene an instance's prefab was opened from.</summary>
public class PrefabStageTests : IDisposable
{
    readonly AssetManager assets = new();
    readonly AssetDatabase database;
    readonly AssetWorkspace workspace;
    readonly PrefabStage stage;

    /// <summary>Creates a workspace with a prefab in the database.</summary>
    public PrefabStageTests()
    {
        TestAssetDatabase.Reset();
        database = new AssetDatabase();
        workspace = new AssetWorkspace(assets, new SettingsService());
        stage = new PrefabStage(workspace, database);
    }

    /// <summary>Releases the subscriptions.</summary>
    public void Dispose()
    {
        stage.Dispose();
        workspace.Dispose();
        TestAssetDatabase.Reset();
        GC.SuppressFinalize(this);
    }

    Guid AddPrefab(string name)
    {
        var id = Guid.NewGuid();
        database.MergeDatabase(new Dictionary<Guid, AssetRecord>
        {
            [id] = new()
            {
                AssetId = id,
                AssetTypeName = typeof(Prefab).FullName!,
                SourceRelativePath = $"Assets/{name}.prefab",
            },
        });
        return id;
    }

    static Node InstanceOf(Guid prefabId) =>
        new() { Name = "Lamp", PrefabInstance = new PrefabInstance { Source = new AssetReference<Prefab>(prefabId) } };

    static Prefab Scene() => new() { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };

    /// <summary>Opening an instance's prefab makes it the active document and remembers the scene.</summary>
    [Fact]
    public void OpenPrefab_RemembersTheScene()
    {
        var scene = workspace.Open(Scene());
        var lampId = AddPrefab("Lamp");

        Assert.True(stage.OpenPrefab(InstanceOf(lampId)));

        Assert.Equal(lampId, workspace.Active!.Asset!.Id);
        Assert.Equal([scene], stage.Trail);
    }

    /// <summary>The breadcrumb goes back to the scene and leaves prefab mode.</summary>
    [Fact]
    public void Return_ActivatesTheSceneAndClearsTheTrail()
    {
        var scene = workspace.Open(Scene());
        stage.OpenPrefab(InstanceOf(AddPrefab("Lamp")));

        stage.Return(0);

        Assert.Same(scene, workspace.Active);
        Assert.Empty(stage.Trail);
    }

    /// <summary>Switching to another document some other way leaves prefab mode.</summary>
    [Fact]
    public void ActivatingAnotherDocument_LeavesPrefabMode()
    {
        var scene = workspace.Open(Scene());
        stage.OpenPrefab(InstanceOf(AddPrefab("Lamp")));

        workspace.Activate(scene);

        Assert.Empty(stage.Trail);
    }

    /// <summary>An instance whose prefab is missing opens nothing.</summary>
    [Fact]
    public void OpenPrefab_MissingPrefab_ReturnsFalse()
    {
        var scene = workspace.Open(Scene());

        Assert.False(stage.OpenPrefab(InstanceOf(Guid.NewGuid())));
        Assert.Same(scene, workspace.Active);
    }
}
