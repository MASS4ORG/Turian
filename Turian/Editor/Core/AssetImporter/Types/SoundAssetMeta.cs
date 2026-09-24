namespace Turian.Editor.Core;

/// <summary>
/// Base metadata contract for sound assets.
/// </summary>
[TypeId("a3000002-0000-4000-8000-000000000003")]
[PublicAPI]
public class SoundAssetMeta : Asset
{
    /// <summary>
    /// Gets or sets the sound format.
    /// </summary>
    public SoundAssetFormat SoundFormat { get; set; } = SoundAssetFormat.Unknown;

    /// <summary>
    /// Gets or sets a value indicating whether the sound should stream.
    /// </summary>
    public bool Stream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sound should loop.
    /// </summary>
    public bool Loop { get; set; }
}
