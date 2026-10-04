using MASS4.Attributes;

namespace Turian.Engine.Hzb;

/// <summary>Opt-in settings for the current-frame GPU occlusion pass.</summary>
[CreateAssetMenu(fileName: "HzbSettings", path: "Settings/GPU Occlusion Culling")]
[TypeId("9bd2b63a-48c6-4f64-8b16-14365e62d8c2")]
public sealed class HzbSettings : ProjectSettingsAsset
{
    internal static readonly Guid PayloadTypeId = new("9bd2b63a-48c6-4f64-8b16-14365e62d8c2");

    /// <summary>Enables HZB for project views with 3D models; disabled by default.</summary>
    public bool Enabled { get; set; }
}
