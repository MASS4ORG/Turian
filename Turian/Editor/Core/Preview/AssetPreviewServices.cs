namespace Turian.Editor.Core;

/// <summary>Binds preview scenes to the render device and asset database used by their viewer.</summary>
public static class AssetPreviewServices
{
    /// <summary>Initializes preview components so their asset references can resolve before rendering.</summary>
    public static void Initialize(AssetPreviewScene scene, Vulkan vulkan, AssetDatabase assets) =>
        scene.Root.Awake(null, new Services(vulkan, assets), allowMissingServices: true);

    sealed class Services(Vulkan vulkan, AssetDatabase assets) : IServiceProvider
    {
        /// <inheritdoc />
        public object? GetService(Type serviceType) => serviceType == typeof(Vulkan) ? vulkan
            : serviceType == typeof(AssetDatabase) ? assets : null;
    }
}
