namespace Turian.Tests;

/// <summary>The renderer casts a KTX2 container's format straight to Vulkan's.</summary>
public class Ktx2VkFormatTests
{
    /// <summary>Every format with a descriptor.</summary>
    public static TheoryData<Ktx2VkFormat> Formats() =>
        [.. Enum.GetValues<Ktx2VkFormat>().Where(Ktx2DataFormat.IsSupported)];

    /// <summary>Every supported value matches the Vulkan enum member of the same name.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public void VkFormat_MatchesTheVulkanEnumValue(Ktx2VkFormat format)
    {
        Assert.True(Enum.TryParse<Format>(format.ToString(), ignoreCase: true, out var vulkan));
        Assert.Equal((uint)format, (uint)vulkan);
    }
}
