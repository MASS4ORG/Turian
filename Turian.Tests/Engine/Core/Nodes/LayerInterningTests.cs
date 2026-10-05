namespace Turian.Tests;

/// <summary>Checks deterministic compact layouts, immutable session mappings, and inline node storage.</summary>
public sealed class LayerInterningTests
{
    /// <summary>Every group fits inline and copying memberships never shares a mutable buffer.</summary>
    [Fact]
    public void NodeLayersAreSixteenInlineBytes()
    {
        Assert.Equal(16, System.Runtime.CompilerServices.Unsafe.SizeOf<NodeLayers>());
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<NodeLayers>());
        var memberships = new NodeLayers();
        for (var slot = 0; slot < NodeLayers.Capacity; slot++) Assert.Equal(0, memberships[slot]);
        memberships[15] = 255;
        var copy = memberships;
        copy[15] = 1;
        Assert.Equal(255, memberships[15]);
        Assert.Equal(1, copy[15]);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[16]);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[-1] = 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[16] = 1);
    }

    /// <summary>All 16 groups and all 256 values map bidirectionally without reserving a byte sentinel.</summary>
    [Fact]
    public void FullLayerCapacityRoundTrips()
    {
        var builder = new LayerRegistrationBuilder("Full");
        for (var slot = 0; slot < 16; slot++)
            builder.Group($"Group{slot}", group =>
            {
                for (var index = 0; index < 256; index++) group.Value($"Value{index}");
            });
        var master = builder.Build();
        var registry = Create(master);
        Assert.Equal(16, registry.GroupCount);
        for (var slot = 0; slot < 16; slot++)
        {
            var group = master.Groups[slot];
            Assert.True(registry.TryGetGroupSlot(group.Id, out var actualSlot));
            Assert.Equal(slot, actualSlot);
            Assert.Equal(group.Id, registry.GetGroupId(slot));
            for (var index = 0; index < 256; index++)
            {
                Assert.True(registry.TryGetLayerIndex(group.Id, group.Values[index].Id, out var actualIndex));
                Assert.Equal(index, actualIndex);
                Assert.Equal(group.Values[index].Id, registry.GetLayerId(slot, index));
            }
        }
    }

    /// <summary>Tag zero and tag 65,535 are valid, and missing names or identities remain distinguishable.</summary>
    [Fact]
    public void FullTagCapacityRoundTrips()
    {
        var master = new MasterNodeLayersAsset
        {
            Tags = [.. Enumerable.Range(0, 65_536).Select(i => new TagAsset { Name = $"Tag{i}" })],
        };
        var registry = Create(master);
        Assert.Equal(65_536, registry.TagCount);
        var sorted = master.Tags.OrderBy(tag => tag.Id).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            Assert.True(registry.TryGetTagId(sorted[i].Id, out var byId));
            Assert.True(registry.TryGetTagId(sorted[i].Name, out var byName));
            Assert.Equal(i, byId);
            Assert.Equal(byId, byName);
            Assert.Equal(sorted[i].Id, registry.GetTagId(byId));
        }
        Assert.False(registry.TryGetTagId(Guid.NewGuid(), out _));
        Assert.False(registry.TryGetTagId("Missing", out _));
    }

    /// <summary>The explicit default always maps to zero even when it appears later in the authored manifest.</summary>
    [Fact]
    public void DefaultsAndIdentitiesSurviveReordering()
    {
        var master = new LayerRegistrationBuilder("Stable")
            .Group("Physics", group => group.Value("Default").Value("Water").Value("Actors")).Build();
        var group = master.Groups[0];
        var defaultId = group.DefaultValue!.Id;
        var waterId = group.Values[1].Id;
        var original = Create(master);
        group.Values.Reverse();
        group.Values.Single(value => value.Id == waterId).Name = "Sea";
        var reordered = Create(master);
        Assert.Equal(defaultId, reordered.GetLayerId(0, 0));
        Assert.True(original.TryGetLayerIndex(group.Id, waterId, out var oldIndex));
        Assert.True(reordered.TryGetLayerIndex(group.Id, waterId, out var newIndex));
        Assert.Equal(waterId, original.GetLayerId(0, oldIndex));
        Assert.Equal(waterId, reordered.GetLayerId(0, newIndex));
    }

    /// <summary>Tag ids are ordered by GUID and survive tag renames and manifest reordering.</summary>
    [Fact]
    public void TagIndicesIgnoreNamesAndEncounterOrder()
    {
        var first = new TagAsset { Id = Guid.Parse("00000001-0000-0000-0000-000000000000"), Name = "Zulu" };
        var second = new TagAsset { Id = Guid.Parse("00000002-0000-0000-0000-000000000000"), Name = "Alpha" };
        var master = new MasterNodeLayersAsset { Tags = [second, first] };
        var original = Create(master);
        first.Name = "Renamed";
        master.Tags.Reverse();
        var reordered = Create(master);
        Assert.True(original.TryGetTagId(first.Id, out var oldId));
        Assert.True(reordered.TryGetTagId(first.Id, out var newId));
        Assert.Equal(0, oldId);
        Assert.Equal(oldId, newId);
        Assert.True(original.TryGetTagId("Zulu", out _));
        Assert.False(original.TryGetTagId("Renamed", out _));
        Assert.True(reordered.TryGetTagId("Renamed", out _));
    }

    /// <summary>A session owns identity mappings independently of mutable assets and rejects cross-group membership.</summary>
    [Fact]
    public void SnapshotDoesNotFollowAssetEdits()
    {
        var master = new LayerRegistrationBuilder("Snapshot")
            .Group("Physics", group => group.Value("Default").Value("Water"))
            .Group("Rendering", group => group.Value("Default")).Tag("Player").Build();
        var group = master.Groups[0];
        var groupId = group.Id;
        var water = group.Values[1];
        var waterId = water.Id;
        var tagId = master.Tags[0].Id;
        var registry = Create(master);
        Assert.False(registry.TryGetLayerIndex(master.Groups[1].Id, waterId, out _));
        Assert.False(registry.TryGetLayerIndex(groupId, Guid.NewGuid(), out _));
        Assert.False(registry.TryGetLayerIndex(Guid.NewGuid(), waterId, out _));
        Assert.False(registry.TryGetGroupSlot(Guid.NewGuid(), out _));
        water.Id = Guid.NewGuid();
        group.Id = Guid.NewGuid();
        master.Groups.Clear();
        master.Tags.Clear();
        Assert.Equal(groupId, registry.GetGroupId(0));
        Assert.Equal(waterId, registry.GetLayerId(0, 1));
        Assert.True(registry.TryGetLayerIndex(groupId, waterId, out _));
        Assert.Equal(tagId, registry.GetTagId(0));
    }

    /// <summary>Zero-master projects have empty tables; malformed masters and absent providers fail before use.</summary>
    [Fact]
    public void EmptyAndInvalidProvidersAreExplicit()
    {
        var empty = Create(null);
        Assert.Equal(0, empty.GroupCount);
        Assert.Equal(0, empty.TagCount);
        Assert.False(empty.TryGetTagId(Guid.NewGuid(), out _));
        Assert.False(empty.TryGetGroupSlot(Guid.NewGuid(), out _));
        Assert.False(empty.TryGetLayerIndex(Guid.NewGuid(), Guid.NewGuid(), out _));
        Assert.Throws<InvalidOperationException>(() => Create(new MasterNodeLayersAsset { Groups = [null!] }));
        Assert.Throws<ArgumentNullException>(() => new LayerInterningService(null!));
    }

    /// <summary>Membership access and identity lookup allocate no memory after a layout has been constructed.</summary>
    [Fact]
    public void RuntimeLookupsDoNotAllocate()
    {
        var master = new LayerRegistrationBuilder("Allocations")
            .Group("Physics", group => group.Value("Default").Value("Water")).Tag("Player").Build();
        var registry = Create(master);
        var groupId = master.Groups[0].Id;
        var valueId = master.Groups[0].Values[1].Id;
        var tagId = master.Tags[0].Id;
        var memberships = new NodeLayers();
        registry.TryGetGroupSlot(groupId, out _);
        registry.TryGetLayerIndex(groupId, valueId, out _);
        registry.TryGetTagId(tagId, out _);
        registry.TryGetTagId("Player", out _);
        memberships[0] = 1;
        _ = memberships[0];
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 10_000; i++)
        {
            registry.TryGetGroupSlot(groupId, out var slot);
            registry.TryGetLayerIndex(groupId, valueId, out var index);
            registry.TryGetTagId(tagId, out _);
            registry.TryGetTagId("Player", out _);
            memberships[slot] = index;
            count += memberships[slot];
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_000, count);
        Assert.Equal(0, allocated);
    }

    static LayerInterningService Create(MasterNodeLayersAsset? master)
    {
        var provider = Substitute.For<ILayerSettingsProvider>();
        provider.Master.Returns(master);
        return new LayerInterningService(provider);
    }
}
