namespace Turian.Tests;

/// <summary>Tests swapchain surface format and composite alpha selection over fake capability sets.</summary>
public sealed class SwapChainSelectionTests
{
    static readonly SurfaceFormatKHR[] DesktopFormats =
    [
        Surface(Format.B8G8R8A8Srgb),
        Surface(Format.B8G8R8A8Unorm),
    ];

    static readonly SurfaceFormatKHR[] AndroidFormats =
    [
        Surface(Format.R8G8B8A8Unorm),
        Surface(Format.R8G8B8A8Srgb),
        Surface(Format.R16G16B16A16Sfloat),
    ];

    /// <summary>Desktop capabilities keep the preferred BGRA sRGB format and opaque alpha.</summary>
    [Fact]
    public void DesktopCapabilitiesKeepBgraSrgbAndOpaque()
    {
        Assert.Equal(
            Surface(Format.B8G8R8A8Srgb),
            SwapChain.ChooseSwapSurfaceFormat(DesktopFormats)
        );
        Assert.Equal(
            CompositeAlphaFlagsKHR.OpaqueBitKhr,
            SwapChain.ChooseCompositeAlpha(CompositeAlphaFlagsKHR.OpaqueBitKhr)
        );
    }

    /// <summary>An Android-like surface picks the offered RGBA sRGB format and the inherited alpha.</summary>
    [Fact]
    public void AndroidCapabilitiesPickRgbaSrgbAndInherit()
    {
        Assert.Equal(
            Surface(Format.R8G8B8A8Srgb),
            SwapChain.ChooseSwapSurfaceFormat(AndroidFormats)
        );
        Assert.Equal(
            CompositeAlphaFlagsKHR.InheritBitKhr,
            SwapChain.ChooseCompositeAlpha(CompositeAlphaFlagsKHR.InheritBitKhr)
        );
    }

    /// <summary>BGRA sRGB wins even when RGBA sRGB appears first in the offered list.</summary>
    [Fact]
    public void BgraSrgbIsPreferredOverRgbaSrgb()
    {
        var formats = new[] { Surface(Format.R8G8B8A8Srgb), Surface(Format.B8G8R8A8Srgb) };

        Assert.Equal(Surface(Format.B8G8R8A8Srgb), SwapChain.ChooseSwapSurfaceFormat(formats));
    }

    /// <summary>The first offered format is used when no sRGB format exists.</summary>
    [Fact]
    public void FormatFallsBackToTheFirstOfferedWhenNoSrgbExists()
    {
        var formats = new[] { Surface(Format.R8G8B8A8Unorm), Surface(Format.B8G8R8A8Unorm) };

        Assert.Equal(Surface(Format.R8G8B8A8Unorm), SwapChain.ChooseSwapSurfaceFormat(formats));
    }

    /// <summary>Composite alpha prefers opaque, then inherit, then any supported bit.</summary>
    [Theory]
    [InlineData(CompositeAlphaFlagsKHR.OpaqueBitKhr, CompositeAlphaFlagsKHR.OpaqueBitKhr)]
    [InlineData(CompositeAlphaFlagsKHR.OpaqueBitKhr | CompositeAlphaFlagsKHR.InheritBitKhr,
        CompositeAlphaFlagsKHR.OpaqueBitKhr)]
    [InlineData(CompositeAlphaFlagsKHR.InheritBitKhr | CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
        CompositeAlphaFlagsKHR.InheritBitKhr)]
    [InlineData(CompositeAlphaFlagsKHR.PreMultipliedBitKhr, CompositeAlphaFlagsKHR.PreMultipliedBitKhr)]
    [InlineData(CompositeAlphaFlagsKHR.PostMultipliedBitKhr, CompositeAlphaFlagsKHR.PostMultipliedBitKhr)]
    [InlineData(0, CompositeAlphaFlagsKHR.OpaqueBitKhr)]
    public void CompositeAlphaFollowsPreferenceOrder(
        CompositeAlphaFlagsKHR supported,
        CompositeAlphaFlagsKHR expected
    )
    {
        Assert.Equal(expected, SwapChain.ChooseCompositeAlpha(supported));
    }

    static SurfaceFormatKHR Surface(
        Format format,
        ColorSpaceKHR colorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr
    ) => new() { Format = format, ColorSpace = colorSpace };
}
