namespace Turian.Engine.Core;

/// <summary>Inline memberships with sparse overflow; zero selects each group's explicit runtime default.</summary>
[JsonConverter(typeof(SessionLayerJsonConverter<NodeLayers>))]
public struct NodeLayers
{
    /// <summary>The number of memberships stored inline, independently of the project's group count.</summary>
    public const int InlineCapacity = 16;

    LayerSlots slots;
    Membership[]? overflow;

    /// <summary>Gets or sets a runtime membership; unassigned slots return zero without allocating.</summary>
    public byte this[int groupSlot]
    {
        readonly get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(groupSlot);
            if (groupSlot < InlineCapacity) return slots[groupSlot];
            var index = Find(overflow, groupSlot);
            return index < 0 ? (byte)0 : overflow![index].Index;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(groupSlot);
            if (groupSlot < InlineCapacity)
            {
                slots[groupSlot] = value;
                return;
            }
            SetOverflow(groupSlot, value);
        }
    }

    void SetOverflow(int slot, byte value)
    {
        var index = Find(overflow, slot);
        if (index < 0)
        {
            if (value != 0) Insert(~index, new Membership(slot, value));
            return;
        }
        if (overflow![index].Index == value) return;
        if (value == 0)
        {
            Remove(index);
            return;
        }

        var next = (Membership[])overflow.Clone();
        next[index] = new Membership(slot, value);
        overflow = next;
    }

    void Insert(int index, Membership membership)
    {
        var entries = overflow.AsSpan();
        var next = new Membership[entries.Length + 1];
        entries[..index].CopyTo(next);
        next[index] = membership;
        entries[index..].CopyTo(next.AsSpan(index + 1));
        overflow = next;
    }

    void Remove(int index)
    {
        if (overflow!.Length == 1)
        {
            overflow = null;
            return;
        }
        var entries = overflow.AsSpan();
        var next = new Membership[entries.Length - 1];
        entries[..index].CopyTo(next);
        entries[(index + 1)..].CopyTo(next.AsSpan(index));
        overflow = next;
    }

    static int Find(ReadOnlySpan<Membership> entries, int slot)
    {
        var start = 0;
        var end = entries.Length - 1;
        while (start <= end)
        {
            var middle = start + (end - start) / 2;
            var key = entries[middle].Slot;
            if (key == slot) return middle;
            if (key < slot) start = middle + 1;
            else end = middle - 1;
        }
        return ~start;
    }

    readonly record struct Membership(int Slot, byte Index);

    [InlineArray(InlineCapacity)]
    struct LayerSlots
    {
        byte first;
    }
}

/// <summary>Rejects durable serialization of layer caches; persist NodeLayerState or LayerMaskState GUIDs.</summary>
public sealed class SessionLayerJsonConverter<T> : JsonConverter<T>
{
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Layer caches are session-scoped; persist NodeLayerState or LayerMaskState GUIDs.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Layer caches are session-scoped; persist NodeLayerState or LayerMaskState GUIDs.");
}
