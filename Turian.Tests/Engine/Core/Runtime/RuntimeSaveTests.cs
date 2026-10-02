namespace Turian.Tests;

/// <summary>Contract tests for runtime save envelopes and session-scoped content resolution.</summary>
public sealed class RuntimeSaveTests
{
    [TypeId("ab11bb0f-220b-4d59-bacf-b2beca477ea0")]
    sealed class StatsAsset : DataAsset
    {
        public int Health { get; set; } = 100;
    }

    static readonly RuntimeSaveContent Content = new()
    {
        Fingerprint = "base-sha256",
        Mods = new Dictionary<string, string> { ["sample.mod"] = "mod-sha256" }
    };

    static RuntimeSaveSnapshot Snapshot(Guid id) => new()
    {
        Tick = 28,
        RngState = 918234,
        State = new JsonObject { ["health"] = 47 },
        ContentIds = [id]
    };

    /// <summary>A fresh session resumes tick, RNG and game state without mutating an authored DataAsset.</summary>
    [Fact]
    public async Task SaveResume_ResolvesSessionContentWithoutSavingAuthoredData()
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Path.GetTempPath(), $"turian-save-{Guid.NewGuid():N}.json");
        try
        {
            var authored = new StatsAsset { Id = id };
            Serializer.Save(path, authored);
            var authoredJson = File.ReadAllText(path);
            var first = new StatsAsset { Id = id, Health = 11 };
            var second = new StatsAsset { Id = id };
            var firstLoader = Substitute.For<IAssetLoader>();
            firstLoader.LoadContentAsync<DataAsset>(id).Returns(Task.FromResult<DataAsset?>(first));
            var secondLoader = Substitute.For<IAssetLoader>();
            secondLoader.LoadContentAsync<DataAsset>(id).Returns(Task.FromResult<DataAsset?>(second));
            var codec = new RuntimeSave();
            var json = codec.Write(Snapshot(id), Content);
            var firstSave = await codec.ReadAsync(json, Content, firstLoader);
            var resumed = await codec.ReadAsync(json, Content, secondLoader);

            Assert.Equal(1, JsonNode.Parse(json)!["Version"]!.GetValue<int>());
            Assert.DoesNotContain("Health", json, StringComparison.Ordinal);
            Assert.Equal(28, resumed.Snapshot.Tick);
            Assert.Equal(918234UL, resumed.Snapshot.RngState);
            Assert.Equal(47, resumed.Snapshot.State["health"]!.GetValue<int>());
            Assert.Same(first, firstSave.Content[id]);
            Assert.Same(second, resumed.Content[id]);
            Assert.NotSame(firstSave.Content[id], resumed.Content[id]);
            Assert.Equal(100, second.Health);
            Assert.Equal(authoredJson, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Unknown optional extension data survives a read; unknown required data blocks resume.</summary>
    [Fact]
    public async Task Extensions_RequireExplicitSupportWhenMarkedRequired()
    {
        var id = Guid.NewGuid();
        var snapshot = Snapshot(id);
        snapshot.Extensions["sample.mod:quest"] = new RuntimeSaveExtension
        {
            Data = new JsonObject { ["stage"] = 2 }
        };
        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<DataAsset>(id).Returns(Task.FromResult<DataAsset?>(new StatsAsset { Id = id }));
        var codec = new RuntimeSave();
        var optional = await codec.ReadAsync(codec.Write(snapshot, Content), Content, loader);
        Assert.Equal(2, optional.Snapshot.Extensions["sample.mod:quest"].Data!["stage"]!.GetValue<int>());
        Assert.Equal(["sample.mod:quest"], optional.UnsupportedOptionalExtensions);

        snapshot.Extensions["sample.mod:quest"].Required = true;
        var json = codec.Write(snapshot, Content);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(json, Content, loader));
        Assert.Contains("sample.mod:quest", error.Message, StringComparison.Ordinal);
        var incomplete = JsonNode.Parse(json)!.AsObject();
        incomplete["Snapshot"]!["Extensions"]!["sample.mod:quest"]!.AsObject().Remove("Required");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => codec.ReadAsync(incomplete.ToJsonString(), Content, loader));
        var supported = await codec.ReadAsync(json, Content, loader, new HashSet<string> { "sample.mod:quest" });
        Assert.True(supported.Snapshot.Extensions["sample.mod:quest"].Required);
        Assert.Empty(supported.UnsupportedOptionalExtensions);
    }

    /// <summary>Version and content mismatches fail before attempting to load any assets.</summary>
    [Fact]
    public async Task Load_RejectsUnsupportedVersionAndFingerprintMismatch()
    {
        var codec = new RuntimeSave();
        var loader = Substitute.For<IAssetLoader>();
        var root = JsonNode.Parse(codec.Write(Snapshot(Guid.NewGuid()), Content))!.AsObject();
        root["Version"] = 2;
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Version"] = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Version"] = "invalid";
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Version"] = new JsonObject();
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Version"] = 999999999999L;
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Version"] = 1;
        root["Snapshot"]!.AsObject().Remove("RngState");
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(root.ToJsonString(), Content, loader));
        root["Snapshot"]!.AsObject()["RngState"] = 918234;
        var wrong = new RuntimeSaveContent { Fingerprint = Content.Fingerprint };
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => codec.ReadAsync(root.ToJsonString(), wrong, loader));
        Assert.Contains("fingerprints", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync("{invalid", Content, loader));
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync("[]", Content, loader));
        await loader.DidNotReceiveWithAnyArgs().LoadContentAsync<DataAsset>(default);
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.RegisterMigration(1, json => json));
    }

    /// <summary>Missing runtime content is reported with the asset id rather than silently omitted.</summary>
    [Fact]
    public async Task Load_RejectsUnresolvableContent()
    {
        var id = Guid.NewGuid();
        var loader = Substitute.For<IAssetLoader>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => new RuntimeSave().ReadAsync(new RuntimeSave().Write(Snapshot(id), Content), Content, loader));
        Assert.Contains(id.ToString(), error.Message, StringComparison.Ordinal);
    }

    /// <summary>Registered migrations run before validation and cannot be silently replaced.</summary>
    [Fact]
    public async Task Load_UsesRegisteredStepwiseMigration()
    {
        var codec = new RuntimeSave();
        var root = JsonNode.Parse(codec.Write(new RuntimeSaveSnapshot(), Content))!.AsObject();
        root["Version"] = 0;
        root["Snapshot"]!.AsObject().Remove("RngState");
        codec.RegisterMigration(0, json =>
        {
            json["Snapshot"]!["RngState"] = 17;
            return json;
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.RegisterMigration(0, json => json));

        var resumed = await codec.ReadAsync(root.ToJsonString(), Content, Substitute.For<IAssetLoader>());

        Assert.Equal(17UL, resumed.Snapshot.RngState);
    }

    /// <summary>A throwing migration is reported with its version before any content is resolved.</summary>
    [Fact]
    public async Task Load_ReportsFailedMigrationWithoutResolvingContent()
    {
        var codec = new RuntimeSave();
        var root = JsonNode.Parse(codec.Write(Snapshot(Guid.NewGuid()), Content))!.AsObject();
        root["Version"] = 0;
        codec.RegisterMigration(0, _ => throw new KeyNotFoundException("legacy field"));
        var loader = Substitute.For<IAssetLoader>();

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => codec.ReadAsync(root.ToJsonString(), Content, loader));

        Assert.Contains("version 0", error.Message, StringComparison.Ordinal);
        Assert.IsType<KeyNotFoundException>(error.InnerException);
        await loader.DidNotReceiveWithAnyArgs().LoadContentAsync<DataAsset>(default);
    }
}
