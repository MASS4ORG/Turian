namespace Turian.Tests;

/// <summary>The shipped shaders load on the Vulkan 1.2 device the renderer creates.</summary>
public sealed class ShaderSpirVVersionTests
{
    // Vulkan 1.2 consumes SPIR-V up to 1.5; the header's second word holds the version as 0x00MMmm00.
    const uint MaxVersion = 0x00010500;

    /// <summary>Every compiled shader of the Vulkan brick targets SPIR-V 1.5 or older.</summary>
    [Fact]
    public void ShadersTargetVulkan12()
    {
        var root = AppContext.BaseDirectory;
        while (Directory.GetFiles(root, "*.slnx").Length == 0)
        {
            root = Directory.GetParent(root)!.FullName;
        }

        var shaders = Directory.GetFiles(Path.Combine(root, "Turian/Packages/org.mass4.turian.vulkan"), "*.spv",
            SearchOption.AllDirectories);

        Assert.NotEmpty(shaders);
        Assert.All(shaders, static shader =>
        {
            var header = File.ReadAllBytes(shader);
            Assert.Equal(0x07230203u, BitConverter.ToUInt32(header, 0));
            Assert.True(BitConverter.ToUInt32(header, 4) <= MaxVersion, $"{Path.GetFileName(shader)} needs SPIR-V > 1.5");
        });
    }
}
