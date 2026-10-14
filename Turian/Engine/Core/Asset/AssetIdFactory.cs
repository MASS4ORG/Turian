using System.Diagnostics.CodeAnalysis;

namespace Turian.Engine.Core;

/// <summary>
/// Generates deterministic <see cref="Guid"/> values for child assets derived
/// from a parent (e.g. the materials and textures inside a glTF file).
/// Re-running the importer on the same source produces the same ids, so
/// references in scenes/prefabs survive re-imports.
/// </summary>
public static class AssetIdFactory
{
    /// <summary>
    /// Derives a v5-style (name-based, SHA-1) <see cref="Guid"/> from
    /// <paramref name="parent"/> and <paramref name="slot"/>. The slot is a
    /// stable string token like <c>"material:0"</c> or <c>"image:2"</c>.
    /// </summary>
    [SuppressMessage("Security", "CA5350:DoNotUseWeakCryptographicAlgorithms",
        Justification = "SHA-1 preserves existing v5-style deterministic asset identities; not used for security.")]
    public static Guid Derive(Guid parent, string slot)
    {
        ArgumentException.ThrowIfNullOrEmpty(slot);

        var input = Encoding.UTF8.GetBytes($"{parent:N}/{slot}");
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        var bytes = hash[..16].ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }
}
