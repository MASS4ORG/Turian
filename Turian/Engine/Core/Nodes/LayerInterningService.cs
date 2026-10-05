namespace Turian.Engine.Core;

/// <summary>Supplies the fully resolved authoring manifest from assets, code, or a test fixture.</summary>
public interface ILayerSettingsProvider
{
    /// <summary>The active settings; null selects an empty runtime layout.</summary>
    NodeLayerSettings? Settings { get; }
}

/// <summary>Maps stable asset identities to compact indices in an immutable session layout.</summary>
public sealed class LayerInterningService
{
    readonly Dictionary<Guid, int> groupSlots = [];
    readonly Dictionary<Guid, (int Slot, byte Index)> layers = [];
    readonly Dictionary<Guid, ushort> tags = [];
    readonly Guid[] groupIds;
    readonly Guid[][] layerIds;
    readonly Guid[] tagIds;

    /// <summary>Validates and snapshots one provider; subsequent asset edits require a new session layout.</summary>
    public LayerInterningService(ILayerSettingsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var master = provider.Settings;
        if (master is null)
        {
            groupIds = [];
            layerIds = [];
            tagIds = [];
            return;
        }

        var errors = master.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        groupIds = [.. master.Groups.Select(group => group.Id)];
        layerIds = new Guid[groupIds.Length][];
        for (var slot = 0; slot < groupIds.Length; slot++) IndexGroup(master.Groups[slot], slot);
        tagIds = IndexTags(master.Tags);
    }

    /// <summary>The number of groups in this session layout.</summary>
    public int GroupCount => groupIds.Length;

    /// <summary>The number of tags in this session layout; all ushort values are available.</summary>
    public int TagCount => tagIds.Length;

    /// <summary>Finds a group's runtime slot by stable identity.</summary>
    public bool TryGetGroupSlot(Guid groupId, out int slot) => groupSlots.TryGetValue(groupId, out slot);

    /// <summary>Finds a group's runtime slot by its typed key.</summary>
    public bool TryGetGroupSlot<TGroup>(out int slot) where TGroup : class, ILayerGroupKey =>
        TryGetGroupSlot(LayerKeyIdentity<TGroup>.Id, out slot);

    /// <summary>Finds a value's runtime index only when it belongs to the requested group.</summary>
    public bool TryGetLayerIndex(Guid groupId, Guid valueId, out byte index)
    {
        index = 0;
        if (!groupSlots.TryGetValue(groupId, out var slot)
            || !layers.TryGetValue(valueId, out var value) || value.Slot != slot) return false;
        index = value.Index;
        return true;
    }

    /// <summary>Finds a value's runtime index by typed keys that declare the same group.</summary>
    public bool TryGetLayerIndex<TGroup, TValue>(out byte index)
        where TGroup : class, ILayerGroupKey where TValue : class, ILayerValueKey<TGroup> =>
        TryGetLayerIndex(LayerKeyIdentity<TGroup>.Id, LayerKeyIdentity<TValue>.Id, out index);

    /// <summary>Gets the stable identity for a runtime group slot.</summary>
    public Guid GetGroupId(int slot) => groupIds[slot];

    /// <summary>Gets a value's stable identity for saving a runtime membership.</summary>
    public Guid GetLayerId(int slot, int index) => layerIds[slot][index];

    /// <summary>Finds a tag's compact id by stable identity; zero is a valid tag id.</summary>
    public bool TryGetTagId(Guid tagId, out ushort index) => tags.TryGetValue(tagId, out index);

    /// <summary>Finds a tag's compact id by its typed key.</summary>
    public bool TryGetTagId<TTag>(out ushort index) where TTag : class, ITagKey =>
        TryGetTagId(LayerKeyIdentity<TTag>.Id, out index);

    /// <summary>Gets a tag's stable identity for saving runtime tags.</summary>
    public Guid GetTagId(ushort index) => tagIds[index];

    Guid[] IndexTags(List<TagAsset> values)
    {
        var ordered = values.OrderBy(tag => tag.Id).ToArray();
        var ids = new Guid[ordered.Length];
        for (var index = 0; index < ordered.Length; index++)
        {
            var tag = ordered[index];
            ids[index] = tag.Id;
            tags.Add(tag.Id, (ushort)index);
        }
        return ids;
    }

    void IndexGroup(LayerGroupAsset group, int slot)
    {
        groupSlots.Add(group.Id, slot);
        var ids = new List<Guid> { group.DefaultValue!.Id };
        ids.AddRange(group.Values.Where(value => value.Id != group.DefaultValue.Id).Select(value => value.Id));
        layerIds[slot] = [.. ids];
        for (var index = 0; index < ids.Count; index++) layers.Add(ids[index], (slot, (byte)index));
    }
}
