namespace Turian.Engine.Hzb;

/// <summary>Creates an optional current-frame depth hierarchy and GPU indirect visibility pass.</summary>
public sealed class HzbCullingFactory : IOcclusionCullingFactory
{
    /// <inheritdoc />
    public bool IsEnabled(AssetDatabase assets)
    {
        foreach (var record in assets.GetAssetsSnapshot())
        {
            if (record.DataAssetPayloadTypeId != HzbSettings.PayloadTypeId) continue;
            var metadata = new DataAssetAsset { Id = record.AssetId, RelativePath = record.SourceRelativePath };
            return metadata.GetContent(record.ProjectRootPath, assets) is HzbSettings { Enabled: true };
        }
        return false;
    }

    /// <inheritdoc />
    public IOcclusionCuller Create(Vulkan vulkan, Silk.NET.Vulkan.DescriptorSetLayout globalLayout) =>
        new HzbCuller(vulkan, globalLayout);
}
