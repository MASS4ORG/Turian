using Turian.Android;

namespace Turian.Tests;

/// <summary>The Android startup feature floor, independently of installed graphics hardware.</summary>
public sealed class AndroidGraphicsSupportTests
{
    /// <summary>Upgraded operating systems cannot make a below-floor graphics driver supported.</summary>
    [Theory]
    [InlineData(1, 1, true, true, true, false)]
    [InlineData(1, 2, true, true, true, false)]
    [InlineData(1, 3, false, true, true, false)]
    [InlineData(1, 3, true, false, true, false)]
    [InlineData(1, 3, true, true, false, false)]
    [InlineData(1, 3, true, true, true, true)]
    [InlineData(1, 4, true, true, true, true)]
    public void RequiresVulkanAndRendererFeatures(uint major, uint minor, bool anisotropy, bool sync,
        bool dynamicRendering, bool supported) =>
        Assert.Equal(supported, AndroidGraphicsSupport.MeetsRequirements((major << 22) | (minor << 12),
            anisotropy, sync, dynamicRendering));
}
