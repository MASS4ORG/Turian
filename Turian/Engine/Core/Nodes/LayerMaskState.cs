namespace Turian.Engine.Core;

/// <summary>A durable selection of values in one group, retaining missing identities when saved.</summary>
public sealed class LayerMaskState
{
    /// <summary>The identity of the group whose values the mask selects.</summary>
    public Guid GroupId { get; set; }

    /// <summary>The selected identities; missing values resolve without bits and remain in this list.</summary>
    public List<Guid> Values { get; set; } = [];

    /// <summary>Whether every currently declared value is selected, including values added in later sessions.</summary>
    public bool Everything { get; set; }

    /// <summary>Compiles a group-bound snapshot mask without changing authored or orphaned references.</summary>
    public CompiledLayerMask Compile(LayerInterningService layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ValidateIdentities();
        if (!layout.TryGetGroupSlot(GroupId, out var slot))
        {
            layout.WarnMissing(new LayerReference(GroupId, Guid.Empty));
            return CompiledLayerMask.Missing;
        }

        var mask = new CompiledLayerMask(slot, layout.GetLayerCount(slot));
        foreach (var value in Values)
        {
            if (layout.TryGetLayerIndex(GroupId, value, out var index)) mask = mask.With(index);
            else layout.WarnMissing(new LayerReference(GroupId, value));
        }
        if (Everything)
            for (var index = 0; index < mask.ValueCount; index++) mask = mask.With((byte)index);
        return mask;
    }

    void ValidateIdentities()
    {
        if (GroupId == Guid.Empty || Values is null || Values.Any(value => value == Guid.Empty))
            throw new InvalidDataException("Layer masks require nonempty group and value identities.");
    }
}

/// <summary>A session-scoped group binding with 256 inline bits for allocation-free membership checks.</summary>
[JsonConverter(typeof(SessionLayerJsonConverter<CompiledLayerMask>))]
public readonly struct CompiledLayerMask
{
    readonly MaskWords words;

    internal static CompiledLayerMask Missing => new(-1, 0);

    internal CompiledLayerMask(int slot, int count)
    {
        GroupSlot = slot;
        ValueCount = count;
    }

    CompiledLayerMask(int slot, int count, MaskWords words)
    {
        GroupSlot = slot;
        ValueCount = count;
        this.words = words;
    }

    /// <summary>The resolved group slot, or -1 when its identity is missing.</summary>
    public int GroupSlot { get; }

    /// <summary>The number of declared values in this mask's snapshot group.</summary>
    public int ValueCount { get; }

    /// <summary>Tests a valid group index against its bit.</summary>
    public bool Contains(byte index) => index < ValueCount && (words[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>Tests the bound membership in one node's runtime cache.</summary>
    public bool Includes(in NodeLayers layers) => GroupSlot >= 0 && Contains(layers[GroupSlot]);

    /// <summary>Packs groups with at most 32 declared values for the legacy GPU path; larger groups are rejected.</summary>
    public bool TryGetLegacyBits(out uint bits)
    {
        bits = 0;
        if (GroupSlot < 0 || ValueCount == 0 || ValueCount > 32) return false;
        bits = (uint)words[0];
        return true;
    }

    internal CompiledLayerMask With(byte index)
    {
        var updated = words;
        updated[index >> 6] |= 1UL << (index & 63);
        return new CompiledLayerMask(GroupSlot, ValueCount, updated);
    }

    [System.Runtime.CompilerServices.InlineArray(4)]
    struct MaskWords
    {
        ulong first;
    }
}
