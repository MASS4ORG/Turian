using static Turian.Tests.LayerTestData;

namespace Turian.Tests;

/// <summary>Checks deterministic layouts, expandable membership storage, and immutable session mappings.</summary>
public sealed class LayerInterningTests
{
    /// <summary>Unassigned storage is 24 bytes on the 64-bit runtime and first memberships remain inline.</summary>
    [Fact]
    public void CommonStorageIsTwentyFourBytes()
    {
        Assert.Equal(16 + IntPtr.Size, Unsafe.SizeOf<NodeLayers>());
        var memberships = new NodeLayers();
        for (var slot = 0; slot < NodeLayers.InlineCapacity; slot++) Assert.Equal(0, memberships[slot]);
        memberships[15] = 255;
        var copy = memberships;
        copy[15] = 1;
        Assert.Equal(255, memberships[15]);
        Assert.Equal(1, copy[15]);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => memberships[-1] = 1);
        Assert.Equal(0, memberships[16]);
        Assert.Equal(0, memberships[int.MaxValue]);
    }

    /// <summary>Sparse entries are ordered and edits preserve independent copies, including clearing defaults.</summary>
    [Fact]
    public void SparseOverflowPreservesCopies()
    {
        var original = new NodeLayers();
        original[1000] = 7;
        original[16] = 255;
        original[32] = 2;
        original[int.MaxValue] = 8;
        var copy = original;
        copy[32] = 3;
        copy[16] = 0;
        copy[1000] = 0;
        Assert.Equal(255, original[16]);
        Assert.Equal(2, original[32]);
        Assert.Equal(7, original[1000]);
        Assert.Equal(0, copy[16]);
        Assert.Equal(3, copy[32]);
        Assert.Equal(0, copy[1000]);
        Assert.Equal(8, copy[int.MaxValue]);
        copy[int.MaxValue] = 0;
        copy[32] = 0;
        Assert.Equal(0, copy[32]);
        Assert.Equal(0, copy[int.MaxValue]);
        copy[1000] = 0;
        copy[17] = 1;
        Assert.Equal(1, copy[17]);
    }

    /// <summary>Projects can register more than 256 groups and use value 255 in any group.</summary>
    [Fact]
    public void GroupsBeyondByteCapacityRoundTrip()
    {
        var settings = new NodeLayerSettings
        {
            Groups = [.. Enumerable.Range(0, 300).Select(index => Group($"Group{index}", 256))],
        };
        var registry = Registry(settings);
        Assert.Equal(300, registry.GroupCount);
        var memberships = new NodeLayers();
        for (var slot = 0; slot < settings.Groups.Count; slot++)
        {
            var group = settings.Groups[slot];
            Assert.True(registry.TryGetGroupSlot(group.Id, out var actualSlot));
            Assert.Equal(slot, actualSlot);
            Assert.Equal(group.Id, registry.GetGroupId(slot));
            for (var index = 0; index < 256; index++)
            {
                Assert.True(registry.TryGetLayerIndex(group.Id, group.Values[index].Id, out var actualIndex));
                Assert.Equal(index, actualIndex);
                Assert.Equal(group.Values[index].Id, registry.GetLayerId(slot, index));
            }
            memberships[slot] = 255;
            Assert.Equal(255, memberships[slot]);
        }
    }

    /// <summary>Both tag zero and 65,535 are valid and all identities round-trip without a sentinel.</summary>
    [Fact]
    public void FullTagCapacityRoundTrips()
    {
        var settings = new NodeLayerSettings
        {
            Tags = [.. Enumerable.Range(0, 65_536).Select(index => new TagAsset { Name = $"Tag{index}" })],
        };
        var registry = Registry(settings);
        Assert.Equal(65_536, registry.TagCount);
        var sorted = settings.Tags.OrderBy(tag => tag.Id).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            Assert.True(registry.TryGetTagId(sorted[index].Id, out var actualIndex));
            Assert.Equal(index, actualIndex);
            Assert.Equal(sorted[index].Id, registry.GetTagId(actualIndex));
        }
        Assert.False(registry.TryGetTagId(Guid.NewGuid(), out _));
    }

    /// <summary>Typed group, value, and tag keys resolve independently of renamed presentation metadata.</summary>
    [Fact]
    public void TypedLookupsResolveIdentities()
    {
        var settings = TypedSettings();
        settings.Groups[0].Name = "Renamed";
        settings.Groups[0].Values[1].Name = "Sea";
        settings.Tags[0].Name = "Hero";
        var registry = Registry(settings);
        Assert.True(registry.TryGetGroupSlot<Physics>(out var slot));
        Assert.Equal(0, slot);
        Assert.True(registry.TryGetLayerIndex<Physics, Water>(out var index));
        Assert.Equal(1, index);
        Assert.True(registry.TryGetLayerIndex<Physics, RenamedWater>(out var renamedIndex));
        Assert.Equal(index, renamedIndex);
        Assert.True(registry.TryGetTagId<Player>(out var tag));
        Assert.Equal(settings.Tags[0].Id, registry.GetTagId(tag));
        Assert.False(registry.TryGetTagId<Enemy>(out _));
        Assert.False(Registry(null).TryGetGroupSlot<Physics>(out _));
        Assert.False(Registry(null).TryGetLayerIndex<Physics, Water>(out _));
    }

    /// <summary>Explicit defaults and stable value identities survive authored manifest reordering.</summary>
    [Fact]
    public void DefaultsAndIdentitiesSurviveReordering()
    {
        var settings = TypedSettings();
        var group = settings.Groups[0];
        var defaultId = group.DefaultValue!.Id;
        var waterId = group.Values[1].Id;
        var original = Registry(settings);
        group.Values.Reverse();
        var reordered = Registry(settings);
        Assert.Equal(defaultId, reordered.GetLayerId(0, 0));
        Assert.True(original.TryGetLayerIndex(group.Id, waterId, out var oldIndex));
        Assert.True(reordered.TryGetLayerIndex(group.Id, waterId, out var newIndex));
        Assert.Equal(waterId, original.GetLayerId(0, oldIndex));
        Assert.Equal(waterId, reordered.GetLayerId(0, newIndex));
    }

    /// <summary>Tag ids are ordered by GUID independently of display names and encounter order.</summary>
    [Fact]
    public void TagIndicesIgnoreNamesAndEncounterOrder()
    {
        var first = new TagAsset { Id = Guid.Parse("00000001-0000-0000-0000-000000000000"), Name = "Zulu" };
        var second = new TagAsset { Id = Guid.Parse("00000002-0000-0000-0000-000000000000"), Name = "Alpha" };
        var settings = new NodeLayerSettings { Tags = [second, first] };
        var original = Registry(settings);
        first.Name = "Renamed";
        settings.Tags.Reverse();
        var reordered = Registry(settings);
        Assert.True(original.TryGetTagId(first.Id, out var oldId));
        Assert.True(reordered.TryGetTagId(first.Id, out var newId));
        Assert.Equal(0, oldId);
        Assert.Equal(oldId, newId);
    }

    /// <summary>A session snapshots identities and rejects memberships in another group.</summary>
    [Fact]
    public void SnapshotDoesNotFollowAssetEdits()
    {
        var settings = TypedSettings();
        var group = settings.Groups[0];
        var groupId = group.Id;
        var water = group.Values[1];
        var waterId = water.Id;
        var tagId = settings.Tags[0].Id;
        var registry = Registry(settings);
        Assert.False(registry.TryGetLayerIndex(settings.Groups[1].Id, waterId, out _));
        Assert.False(registry.TryGetLayerIndex(groupId, Guid.NewGuid(), out _));
        Assert.False(registry.TryGetLayerIndex(Guid.NewGuid(), waterId, out _));
        Assert.False(registry.TryGetGroupSlot(Guid.NewGuid(), out _));
        water.Id = Guid.NewGuid();
        group.Id = Guid.NewGuid();
        settings.Groups.Clear();
        settings.Tags.Clear();
        Assert.Equal(groupId, registry.GetGroupId(0));
        Assert.Equal(waterId, registry.GetLayerId(0, 1));
        Assert.True(registry.TryGetLayerIndex(groupId, waterId, out _));
        Assert.Equal(tagId, registry.GetTagId(0));
    }

    /// <summary>Missing settings produce empty tables while invalid manifests and absent providers fail before use.</summary>
    [Fact]
    public void EmptyAndInvalidProvidersAreExplicit()
    {
        var empty = Registry(null);
        Assert.Equal(0, empty.GroupCount);
        Assert.Equal(0, empty.TagCount);
        Assert.False(empty.TryGetTagId(Guid.NewGuid(), out _));
        Assert.False(empty.TryGetGroupSlot(Guid.NewGuid(), out _));
        Assert.False(empty.TryGetLayerIndex(Guid.NewGuid(), Guid.NewGuid(), out _));
        Assert.Throws<InvalidOperationException>(() => Registry(new NodeLayerSettings { Groups = [null!] }));
        Assert.Throws<ArgumentNullException>(() => new LayerInterningService(null!));
    }

    /// <summary>Inline writes, sparse reads and unchanged assignments, and typed lookups allocate no memory.</summary>
    [Fact]
    public void RuntimeLookupsDoNotAllocate()
    {
        var registry = Registry(TypedSettings());
        var memberships = new NodeLayers();
        registry.TryGetGroupSlot<Physics>(out _);
        registry.TryGetLayerIndex<Physics, Water>(out _);
        registry.TryGetTagId<Player>(out _);
        memberships[1000] = 255;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            registry.TryGetGroupSlot<Physics>(out var slot);
            registry.TryGetLayerIndex<Physics, Water>(out var index);
            registry.TryGetTagId<Player>(out _);
            memberships[slot] = index;
            memberships[1000] = 255;
            memberships[2000] = 0;
            count += memberships[slot] + memberships[1000];
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2_560_000, count);
        Assert.Equal(0, allocated);
    }
}
