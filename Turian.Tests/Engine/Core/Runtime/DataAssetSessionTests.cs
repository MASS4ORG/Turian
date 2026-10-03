namespace Turian.Tests;

/// <summary>Probes mutable DataAssets wired by identity across runtime and test sessions.</summary>
[Collection(SerialTests.Name)]
public sealed class DataAssetSessionTests : IDisposable
{
    readonly string projectRoot = Path.Combine(Path.GetTempPath(), $"turian-da-session-{Guid.NewGuid():N}");

    [TypeId("02916fc5-7f40-46cc-9a0e-6edf0e045cea")]
    sealed class GameManagerAsset : DataAsset
    {
        public int Coins { get; set; } = 10;
    }

    [TypeId("090df493-cc62-4716-b98d-e3e05632758c")]
    sealed class ManagerConsumer : Component
    {
        [InjectService, JsonIgnore]
        public IAssetLoader? Assets { get; private set; }

        public DataAssetReference<GameManagerAsset> Manager { get; set; } = new();

        public Task<GameManagerAsset?> LoadManagerAsync() =>
            Manager.LoadContentAsync(Assets ?? throw new InvalidOperationException("Asset loader unavailable."));
    }

    [TypeId("1112d5e7-967a-45bc-9e94-f7e207db8c0c")]
    sealed class DirectManagerConsumer : Component
    {
        public GameManagerAsset? Manager { get; set; }

        [JsonIgnore]
        public bool HadManagerAtAwake { get; private set; }

        public override void OnAwake() => HadManagerAtAwake = Manager is not null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
    }

    /// <summary>
    /// Two sessions use the same asset ID, share it within each session and isolate mutations
    /// from each other and from authored data; a save restores state only when applied explicitly.
    /// </summary>
    [Fact]
    public async Task TwoSessions_SharedGuidIndependentStateAndExplicitSave()
    {
        var (database, assetId, sourcePath) = CreateAuthoredManager();
        var townJson = CreateSceneJson(assetId, "Town");
        var dungeonJson = CreateSceneJson(assetId, "Dungeon");

        using var firstServices = new ServiceCollection()
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database)).BuildServiceProvider();
        using var secondServices = new ServiceCollection()
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database)).BuildServiceProvider();
        var firstSceneManager = new SceneManager(database);
        var firstTown = LoadScene(townJson, firstServices);
        firstSceneManager.AdoptScene(Guid.NewGuid(), firstTown);
        var firstManager = (await firstTown.GetComponentsInChildren<ManagerConsumer>()
            .Single().LoadManagerAsync())!;
        firstManager.Coins = 45;

        var firstDungeon = LoadScene(dungeonJson, firstServices);
        firstSceneManager.AdoptScene(Guid.NewGuid(), firstDungeon);
        var dungeonManager = (await firstDungeon.GetComponentsInChildren<ManagerConsumer>()
            .Single().LoadManagerAsync())!;
        var secondManager = (await LoadScene(townJson, secondServices)
            .GetComponentsInChildren<ManagerConsumer>().Single().LoadManagerAsync())!;

        Assert.Same(firstManager, dungeonManager);
        Assert.NotSame(firstManager, secondManager);
        Assert.Equal(10, secondManager.Coins);
        Assert.Equal(45, dungeonManager.Coins);
        Assert.Equal(10, secondManager.Coins);
        Assert.Equal(10, ((GameManagerAsset)DataAsset.LoadContent(sourcePath)!).Coins);

        var savePath = Path.Combine(projectRoot, "save.json");
        Serializer.Save(savePath, new SavedGame { Coins = firstManager.Coins });
        Serializer.ResetOptions();
        using var nextServices = new ServiceCollection()
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database)).BuildServiceProvider();
        var nextManager = (await LoadScene(townJson, nextServices)
            .GetComponentsInChildren<ManagerConsumer>().First().LoadManagerAsync())!;
        Assert.Equal(10, nextManager.Coins);
        nextManager.Coins = Serializer.Load<SavedGame>(savePath)!.Coins;
        Assert.Equal(45, nextManager.Coins);
        Assert.Equal(10, ((GameManagerAsset)DataAsset.LoadContent(sourcePath)!).Coins);
    }

    /// <summary>A fake loader replaces the manager behind the original GUID without rewiring consumers.</summary>
    [Fact]
    public async Task SameGuidFake_ReplacesAllConsumersWithoutSceneEdits()
    {
        var assetId = Guid.NewGuid();
        var fake = new GameManagerAsset { Coins = 99 };
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<GameManagerAsset>(assetId).Returns(Task.FromResult<GameManagerAsset?>(fake));
        using var services = new ServiceCollection().AddSingleton(loader).BuildServiceProvider();

        var consumers = new[] { "Town", "Dungeon" }
            .Select(scene => LoadScene(CreateSceneJson(assetId, scene), services)
                .GetComponentsInChildren<ManagerConsumer>().Single())
            .ToArray();

        Assert.Equal(2, consumers.Length);
        Assert.Equal(assetId, consumers[0].Manager.AssetId);
        var resolved = await Task.WhenAll(consumers.Select(consumer => consumer.LoadManagerAsync()));
        Assert.All(resolved, manager => Assert.Same(fake, manager));
    }

    /// <summary>A direct asset field needs explicit resolution before Awake to be independent of globals.</summary>
    [Fact]
    public void DirectField_UsesExplicitLoaderWithoutInjectingTheConsumer()
    {
        var assetId = Guid.NewGuid();
        var authored = new Node();
        authored.Components.Add(new DirectManagerConsumer
        {
            Manager = new GameManagerAsset { Id = assetId }
        });
        var json = Serializer.Serialize(authored);
        Assert.Contains("\"$ref\"", json, StringComparison.Ordinal);

        var fake = new GameManagerAsset { Coins = 99 };
        var loader = Substitute.For<IAssetLoader>();
        loader.PreloadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        loader.LoadContentAsync<DataAsset>(assetId).Returns(Task.FromResult<DataAsset?>(fake));
        var loaded = Serializer.LoadData<Node>(json)!;
        Assert.Null(loaded.GetComponent<DirectManagerConsumer>()!.Manager);

        Assert.Equal(0, ObjectReferences.Resolve(loaded, loader));
        loaded.Awake(null);

        var consumer = loaded.GetComponent<DirectManagerConsumer>()!;
        Assert.Same(fake, consumer.Manager);
        Assert.True(consumer.HadManagerAtAwake);
    }

    /// <summary>Concurrent scenes resolve direct assets through their own bound session loaders.</summary>
    [Fact]
    public async Task SceneManager_ResolvesDirectAssetFromItsBoundSession()
    {
        var (database, assetId, _) = CreateAuthoredManager();
        var (prefab, sceneJson) = CreateDirectScene(database, assetId);

        var withoutLoader = Serializer.LoadData<Node>(sceneJson)!;
        Assert.Null(withoutLoader.GetComponent<DirectManagerConsumer>()!.Manager);
        Assert.True(ObjectReferences.TryGetUnresolved(
            withoutLoader.GetComponent<DirectManagerConsumer>()!, nameof(DirectManagerConsumer.Manager), out _));

        using var sessionServices = new ServiceCollection()
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database)).BuildServiceProvider();
        var manager = new SceneManager(database);
        manager.BindServices(sessionServices);
        using var secondSessionServices = new ServiceCollection()
            .AddSingleton<IAssetLoader>(new RuntimeAssetLoader(database)).BuildServiceProvider();
        var secondManager = new SceneManager(database);
        secondManager.BindServices(secondSessionServices);

        var loaded = await Task.WhenAll(manager.LoadNodeAsync(prefab.Id), secondManager.LoadNodeAsync(prefab.Id));
        var first = loaded[0].GetComponent<DirectManagerConsumer>()!;
        var second = loaded[1].GetComponent<DirectManagerConsumer>()!;
        Assert.True(first.HadManagerAtAwake);
        Assert.True(second.HadManagerAtAwake);
        Assert.NotSame(first.Manager, second.Manager);
        first.Manager!.Coins = 42;
        Assert.Equal(10, second.Manager!.Coins);
    }

    /// <summary>Editor previews can resolve authored data without injecting gameplay services.</summary>
    [Fact]
    public async Task EditorPreview_BindsOnlyItsAssetLoader()
    {
        var (database, assetId, _) = CreateAuthoredManager();
        var (prefab, _) = CreateDirectScene(database, assetId);
        var loader = new RuntimeAssetLoader(database);
        var preview = new SceneManager(database);
        preview.BindAssetLoader(loader);

        var loaded = await preview.LoadNodeAsync(prefab.Id);
        var component = loaded.GetComponent<DirectManagerConsumer>()!;

        Assert.True(component.HadManagerAtAwake);
        Assert.Null(component.Services);
        Assert.Same(await loader.LoadContentAsync<GameManagerAsset>(assetId), component.Manager);
    }

    /// <summary>A headless editor preview can replace a direct asset by GUID without gameplay DI.</summary>
    [Fact]
    public async Task EditorPreview_UsesFakeAssetWithTheAuthoredGuid()
    {
        var (database, assetId, sourcePath) = CreateAuthoredManager();
        var (prefab, _) = CreateDirectScene(database, assetId);
        var replacement = new GameManagerAsset { Id = assetId, Coins = 77 };
        var loader = Substitute.For<IAssetLoader>();
        loader.PreloadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        loader.LoadContentAsync<DataAsset>(assetId).Returns(Task.FromResult<DataAsset?>(replacement));
        var preview = new SceneManager(database);
        preview.BindAssetLoader(loader);

        var loaded = await preview.LoadNodeAsync(prefab.Id);
        var component = loaded.GetComponent<DirectManagerConsumer>()!;

        Assert.Same(replacement, component.Manager);
        Assert.True(component.HadManagerAtAwake);
        Assert.Null(component.Services);
        Assert.Equal(10, ((GameManagerAsset)DataAsset.LoadContent(sourcePath)!).Coins);
    }

    /// <summary>A play-scene clone resolves direct DataAssets using its explicit session loader.</summary>
    [Fact]
    public void DeepClone_ResolvesDirectAssetsThroughItsExplicitLoader()
    {
        var assetId = Guid.NewGuid();
        var authored = new Node();
        authored.Components.Add(new DirectManagerConsumer
        {
            Manager = new GameManagerAsset { Id = assetId }
        });
        var right = Substitute.For<IAssetLoader>();
        right.PreloadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        right.LoadContentAsync<DataAsset>(assetId)
            .Returns(Task.FromResult<DataAsset?>(new GameManagerAsset { Coins = 37 }));

        var clone = NodeCloner.DeepClone(authored, awake: false, loader: right)!;
        clone.Awake(null);

        var component = clone.GetComponent<DirectManagerConsumer>()!;
        Assert.True(component.HadManagerAtAwake);
        Assert.Equal(37, component.Manager!.Coins);
    }

    (AssetDatabase Database, Guid AssetId, string SourcePath) CreateAuthoredManager()
    {
        TestAssetDatabase.Reset();
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        var sourcePath = Path.Combine(projectRoot, "Assets", "GameManager.dataasset");
        Serializer.Save<DataAsset>(sourcePath, new GameManagerAsset());
        var metadata = new DataAssetAsset { RelativePath = "Assets/GameManager.dataasset" };
        Serializer.Save($"{sourcePath}.meta", metadata);
        var database = new AssetDatabase();
        Assert.True(database.RegisterAsset(new DataAssetAsset { Id = metadata.Id, RelativePath = sourcePath }));
        return (database, metadata.Id, sourcePath);
    }

    (Prefab Prefab, string Json) CreateDirectScene(AssetDatabase database, Guid assetId)
    {
        var authored = new Node();
        authored.Components.Add(new DirectManagerConsumer
        {
            Manager = new GameManagerAsset { Id = assetId }
        });
        var json = Serializer.Serialize(authored);
        var scenePath = Path.Combine(projectRoot, "Assets", "Scene.prefab");
        File.WriteAllText(scenePath, json);
        var prefab = new Prefab { RelativePath = scenePath };
        Serializer.Save($"{scenePath}.meta", new Prefab { Id = prefab.Id, RelativePath = "Assets/Scene.prefab" });
        Assert.True(database.RegisterAsset(prefab));
        return (prefab, json);
    }

    static string CreateSceneJson(Guid assetId, string sceneName)
    {
        var scene = new Node { Name = sceneName };
        var child = new Node { Name = "Consumer" };
        child.Components.Add(new ManagerConsumer
        {
            Manager = new DataAssetReference<GameManagerAsset>(assetId)
        });
        scene.Children.Add(child);

        return Serializer.Serialize(scene);
    }

    static Node LoadScene(string sceneJson, IServiceProvider services)
    {
        var scene = Serializer.LoadData<Node>(sceneJson)!;
        scene.Awake(null, services);
        return scene;
    }

    sealed class SavedGame
    {
        public int Coins { get; set; }
    }
}
