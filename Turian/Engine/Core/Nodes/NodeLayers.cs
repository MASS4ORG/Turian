namespace Turian.Engine.Core;

/// <summary>Sixteen inline layer indices; zero selects each group's explicit default in a runtime layout.</summary>
public struct NodeLayers
{
    /// <summary>The maximum number of independent groups in one runtime layout.</summary>
    public const int Capacity = 16;

    LayerSlots slots;

    /// <summary>Gets or sets a group's runtime index; slots are derived from the master manifest.</summary>
    public byte this[int groupSlot]
    {
        readonly get
        {
            ValidateSlot(groupSlot);
            return slots[groupSlot];
        }
        set
        {
            ValidateSlot(groupSlot);
            slots[groupSlot] = value;
        }
    }

    static void ValidateSlot(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Capacity);
    }

    [System.Runtime.CompilerServices.InlineArray(Capacity)]
    struct LayerSlots
    {
        byte first;
    }
}
