using static Turian.Tests.LayerTestData;

namespace Turian.Tests;

/// <summary>Checks durable memberships, orphan retention, explicit defaults, and mask capabilities.</summary>
[Collection(SerialTests.Name)]
public sealed class LayerPersistenceTests
{
    /// <summary>Configured physics and rendering groups warn beyond slot 15, independently of their display names.</summary>
    [Fact]
    public void HotConsumerGroupsWarnWhenTheyOverflow()
    {
        var factoryField = typeof(Log).GetField("_factory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (ILoggerFactory)factoryField.GetValue(null)!;
        var factory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        Log.Configure(factory);
        try
        {
            var settings = new NodeLayerSettings
            {
                Groups = [.. Enumerable.Range(0, 18).Select(index => Group($"Group{index}"))],
            };
            settings.PhysicsGroup = settings.Groups[15];
            settings.RenderingGroup = settings.Groups[0];
            Registry(settings);
            Assert.DoesNotContain(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
            settings.PhysicsGroup = settings.Groups[16];
            settings.RenderingGroup = settings.Groups[17];
            settings.PhysicsGroup.Name = "Renamed";
            var layout = Registry(settings);
            var warnings = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").ToArray();
            Assert.Equal(2, warnings.Length);
            Assert.All(warnings, call => Assert.Equal(LogLevel.Warning, call.GetArguments()[0]));
            Assert.Contains(warnings, call => call.GetArguments()[2]!.ToString()!.Contains("Physics", StringComparison.Ordinal));
            Assert.Contains(warnings, call => call.GetArguments()[2]!.ToString()!.Contains("Rendering", StringComparison.Ordinal));
            Assert.True(layout.TryGetGroupSlot(settings.PhysicsGroup.Id, out var slot));
            Assert.Equal(16, slot);
        }
        finally { Log.Configure(previous); }
    }

    /// <summary>An explicit default stays assigned when the fallback changes while an unassigned node follows it.</summary>
    [Fact]
    public void ExplicitDefaultAndUnassignedRemainDistinct()
    {
        var settings = TypedSettings();
        var group = settings.Groups[0];
        var originalDefault = group.DefaultValue!.Id;
        var explicitState = new NodeLayerState();
        explicitState.SetLayer(group.Id, originalDefault);
        var saved = Serializer.Serialize(explicitState);
        var restored = Serializer.LoadData<NodeLayerState>(saved)!;
        Assert.Contains(originalDefault.ToString(), saved);
        group.DefaultValue = group.Values[1];
        var layout = Registry(settings);
        var explicitLayers = restored.ResolveLayers(layout);
        var unassignedLayers = new NodeLayerState().ResolveLayers(layout);
        Assert.Equal(originalDefault, layout.GetLayerId(0, explicitLayers[0]));
        Assert.Equal(group.DefaultValue.Id, layout.GetLayerId(0, unassignedLayers[0]));
        restored.ClearLayer(group.Id);
        Assert.Empty(restored.Layers);
        Assert.Equal(0, restored.ResolveLayers(layout)[0]);
        restored.SetLayer(group.Id, originalDefault);
        restored.SetLayer(group.Id, group.Values[2].Id);
        Assert.Single(restored.Layers);
        Assert.Equal(group.Values[2].Id, restored.Layers[0].ValueId);
    }

    /// <summary>Scene and gameplay-save state carry GUID pairs and resolve anew after an inserted value shifts indices.</summary>
    [Fact]
    public async Task RuntimeSavePersistsIdentitiesAcrossInsertion()
    {
        var settings = TypedSettings();
        var group = settings.Groups[0];
        var water = group.Values[1].Id;
        var state = new NodeLayerState { Tags = [settings.Tags[0].Id] };
        state.SetLayer(group.Id, water);
        var oldLayout = Registry(settings);
        Assert.Equal(1, state.ResolveLayers(oldLayout)[0]);
        var snapshot = new RuntimeSaveSnapshot();
        snapshot.State["NodeLayers"] = JsonSerializer.SerializeToNode(state);
        var content = new RuntimeSaveContent { Fingerprint = "stable-guid-content" };
        var codec = new RuntimeSave();
        var saved = codec.Write(snapshot, content);
        group.Values.Insert(1, new LayerValueAsset { Name = "Inserted" });
        var layout = Registry(settings);
        var loaded = await codec.ReadAsync(saved, content, Substitute.For<IAssetLoader>());
        var restored = loaded.Snapshot.State["NodeLayers"]!.Deserialize<NodeLayerState>()!;
        var memberships = restored.ResolveLayers(layout);
        Assert.Equal(2, memberships[0]);
        Assert.Equal(water, layout.GetLayerId(0, memberships[0]));
        Assert.Equal([state.Tags[0]], restored.Tags);
        Assert.Equal([0], restored.ResolveTags(layout).Select(id => (int)id));
        Assert.Contains(group.Id.ToString(), saved);
        Assert.Contains(water.ToString(), saved);
        Assert.DoesNotContain("GroupSlot", saved);
    }

    /// <summary>Missing node, tag, and mask GUIDs survive save/load, warn once, and resolve if their content returns.</summary>
    [Fact]
    public void OrphansSurviveRoundTripsAndWarnOnce()
    {
        var factoryField = typeof(Log).GetField("_factory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (ILoggerFactory)factoryField.GetValue(null)!;
        var factory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        Log.Configure(factory);
        try
        {
            var settings = TypedSettings();
            var group = settings.Groups[0];
            var water = group.Values[1];
            var tag = settings.Tags[0];
            var state = new NodeLayerState { Tags = [tag.Id, tag.Id] };
            state.SetLayer(group.Id, water.Id);
            var mask = new LayerMaskState { GroupId = group.Id, Values = [water.Id] };
            group.Values.Remove(water);
            settings.Tags.Clear();
            var layout = Registry(settings);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                Assert.Equal(0, state.ResolveLayers(layout)[0]);
                Assert.Empty(state.ResolveTags(layout));
                Assert.False(mask.Compile(layout).Contains(0));
            }
            Assert.Equal(2, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log"));
            var restoredState = Serializer.LoadData<NodeLayerState>(Serializer.Serialize(state))!;
            var restoredMask = Serializer.LoadData<LayerMaskState>(Serializer.Serialize(mask))!;
            Assert.Equal(water.Id, restoredState.Layers[0].ValueId);
            Assert.Equal([water.Id], restoredMask.Values);
            Assert.Equal([tag.Id, tag.Id], restoredState.Tags);
            group.Values.Add(water);
            settings.Tags.Add(tag);
            var recovered = Registry(settings);
            Assert.Equal(water.Id, recovered.GetLayerId(0, restoredState.ResolveLayers(recovered)[0]));
            Assert.Single(restoredState.ResolveTags(recovered));
            Assert.True(recovered.TryGetLayerIndex(group.Id, water.Id, out var waterIndex));
            Assert.True(restoredMask.Compile(recovered).Contains(waterIndex));
            var absent = Registry(null);
            Assert.Equal(0, restoredState.ResolveLayers(absent)[0]);
            var missing = restoredMask.Compile(absent);
            Assert.Equal(-1, missing.GroupSlot);
            Assert.False(missing.Includes(default));
            Assert.False(missing.TryGetLegacyBits(out _));
        }
        finally { Log.Configure(previous); }
    }

    /// <summary>All 256 mask bits work and GPU packing is allowed only for groups with at most 32 declared values.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(256)]
    public void GroupWidthControlsLegacyGpuPacking(int count)
    {
        var group = Group("Rendering", count);
        var layout = Registry(new NodeLayerSettings { Groups = [group] });
        var state = new LayerMaskState { GroupId = group.Id, Everything = true };
        var mask = state.Compile(layout);
        Assert.Equal(count, mask.ValueCount);
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<CompiledLayerMask>());
        for (var index = 0; index < 256; index++) Assert.Equal(index < count, mask.Contains((byte)index));
        var memberships = new NodeLayers();
        memberships[0] = (byte)(count - 1);
        Assert.True(mask.Includes(memberships));
        Assert.Equal(count <= 32, mask.TryGetLegacyBits(out var bits));
        Assert.Equal(count == 32 ? uint.MaxValue : count == 1 ? 1u : 0u, bits);
        var nothing = new LayerMaskState { GroupId = group.Id }.Compile(layout);
        Assert.False(nothing.Includes(memberships));
        Assert.False(default(CompiledLayerMask).TryGetLegacyBits(out _));
    }

    /// <summary>Session caches cannot be serialized as durable memberships or masks.</summary>
    [Fact]
    public void RuntimeCacheSerializationIsRejected()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new NodeLayers()));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<NodeLayers>("{}"));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(default(CompiledLayerMask)));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<CompiledLayerMask>("{}"));
    }

    /// <summary>Malformed identity states fail before they can bind invalid or duplicate memberships.</summary>
    [Fact]
    public void InvalidIdentityStatesAreRejected()
    {
        var layout = Registry(null);
        var state = new NodeLayerState();
        Assert.Throws<ArgumentException>(() => state.SetLayer(Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => state.SetLayer(Guid.NewGuid(), Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => state.ResolveLayers(null!));
        Assert.Throws<ArgumentNullException>(() => state.ResolveTags(null!));
        state.Layers = [new LayerReference(Guid.Empty, Guid.NewGuid())];
        Assert.Throws<InvalidDataException>(() => state.ResolveLayers(layout));
        state.Layers = [new LayerReference(Guid.NewGuid(), Guid.Empty)];
        Assert.Throws<InvalidDataException>(() => state.ResolveLayers(layout));
        var assignment = new LayerReference(Guid.NewGuid(), Guid.NewGuid());
        state.Layers = [assignment, assignment];
        Assert.Throws<InvalidDataException>(() => state.ResolveLayers(layout));
        state.Layers = null!;
        Assert.Throws<InvalidDataException>(() => state.ResolveLayers(layout));
        state.Tags = [Guid.Empty];
        Assert.Throws<InvalidDataException>(() => state.ResolveTags(layout));
        state.Tags = null!;
        Assert.Throws<InvalidDataException>(() => state.ResolveTags(layout));
        var mask = new LayerMaskState();
        Assert.Throws<ArgumentNullException>(() => mask.Compile(null!));
        Assert.Throws<InvalidDataException>(() => mask.Compile(layout));
        mask.GroupId = Guid.NewGuid();
        mask.Values = [Guid.Empty];
        Assert.Throws<InvalidDataException>(() => mask.Compile(layout));
        mask.Values = null!;
        Assert.Throws<InvalidDataException>(() => mask.Compile(layout));
    }

    /// <summary>Compiled mask checks allocate nothing after binding.</summary>
    [Fact]
    public void CompiledMasksDoNotAllocateDuringReads()
    {
        var group = Group("Rendering", 256);
        var mask = new LayerMaskState { GroupId = group.Id, Values = [group.Values[255].Id] }
            .Compile(Registry(new NodeLayerSettings { Groups = [group] }));
        var memberships = new NodeLayers();
        memberships[0] = 255;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var matches = 0;
        for (var index = 0; index < 10_000; index++)
            if (mask.Includes(memberships)) matches++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_000, matches);
        Assert.Equal(0, allocated);
    }
}
