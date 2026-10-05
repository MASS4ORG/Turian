using System.Buffers.Binary;

namespace Turian.Engine.Core;

static class LayerLayoutFingerprint
{
    internal static string Compute(Guid[] groups, Guid[][] values, Guid[] tags, Guid physicsGroup = default,
        Guid renderingGroup = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("turian-layer-layout-v2"u8);
        AppendInt(hash, groups.Length);
        for (var slot = 0; slot < groups.Length; slot++)
        {
            AppendInt(hash, slot);
            AppendGuid(hash, groups[slot]);
            AppendInt(hash, values[slot].Length);
            for (var index = 0; index < values[slot].Length; index++)
            {
                AppendInt(hash, index);
                AppendGuid(hash, values[slot][index]);
            }
        }
        AppendInt(hash, tags.Length);
        for (var index = 0; index < tags.Length; index++)
        {
            AppendInt(hash, index);
            AppendGuid(hash, tags[index]);
        }
        AppendGuid(hash, physicsGroup);
        AppendGuid(hash, renderingGroup);
        return "v2:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    static void AppendGuid(IncrementalHash hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        hash.AppendData(bytes);
    }
}
