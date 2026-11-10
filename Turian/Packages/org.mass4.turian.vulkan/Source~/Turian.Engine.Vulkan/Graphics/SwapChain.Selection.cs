namespace Turian.Engine.Core;

public partial class SwapChain
{
    static readonly Format[] PreferredSrgbFormats = [Format.B8G8R8A8Srgb, Format.R8G8B8A8Srgb];

    static readonly CompositeAlphaFlagsKHR[] CompositeAlphaPreference =
    [
        CompositeAlphaFlagsKHR.OpaqueBitKhr,
        CompositeAlphaFlagsKHR.InheritBitKhr,
        CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
        CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
    ];

    internal static SurfaceFormatKHR ChooseSwapSurfaceFormat(
        IReadOnlyList<SurfaceFormatKHR> availableFormats
    )
    {
        foreach (var preferredFormat in PreferredSrgbFormats)
        {
            foreach (var availableFormat in availableFormats)
            {
                if (
                    availableFormat.Format == preferredFormat
                    && availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr
                )
                {
                    return availableFormat;
                }
            }
        }

        return availableFormats[0];
    }

    internal static CompositeAlphaFlagsKHR ChooseCompositeAlpha(
        CompositeAlphaFlagsKHR supportedCompositeAlpha
    )
    {
        foreach (var candidate in CompositeAlphaPreference)
        {
            if (supportedCompositeAlpha.HasFlag(candidate))
            {
                return candidate;
            }
        }

        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        if (UseFifo)
        {
            return PresentModeKHR.FifoKhr;
        }

        foreach (var availablePresentMode in availablePresentModes)
        {
            if (availablePresentMode == PresentModeKHR.MailboxKhr)
            {
                Log.Logger.Lap("swapchain", $"got present mode = Mailbox");
                return availablePresentMode;
            }
        }

        Log.Logger.Lap("swapchain", $"fallback present mode = FifoKhr");
        return PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// The transform images are presented with. The renderer draws upright, so identity is taken when offered and the
    /// compositor turns the picture to match the device.
    /// </summary>
    /// <param name="capabilities">The surface's capabilities.</param>
    /// <returns>The transform to create the swapchain with.</returns>
    internal static SurfaceTransformFlagsKHR ChoosePreTransform(SurfaceCapabilitiesKHR capabilities) =>
        capabilities.SupportedTransforms.HasFlag(SurfaceTransformFlagsKHR.IdentityBitKhr)
            ? SurfaceTransformFlagsKHR.IdentityBitKhr
            : capabilities.CurrentTransform;

    /// <summary>
    /// The surface's extent as upright images need it. A surface turned a quarter reports its extent in the display's
    /// native orientation, which is swapped relative to what the renderer draws.
    /// </summary>
    /// <param name="capabilities">The surface's capabilities.</param>
    /// <returns>The extent to create the swapchain with.</returns>
    internal static Extent2D UprightExtent(SurfaceCapabilitiesKHR capabilities)
    {
        const SurfaceTransformFlagsKHR quarterTurns = SurfaceTransformFlagsKHR.Rotate90BitKhr
            | SurfaceTransformFlagsKHR.Rotate270BitKhr
            | SurfaceTransformFlagsKHR.HorizontalMirrorRotate90BitKhr
            | SurfaceTransformFlagsKHR.HorizontalMirrorRotate270BitKhr;
        var extent = capabilities.CurrentExtent;
        return ChoosePreTransform(capabilities) == SurfaceTransformFlagsKHR.IdentityBitKhr
               && (capabilities.CurrentTransform & quarterTurns) != 0
            ? new Extent2D(extent.Height, extent.Width)
            : extent;
    }

    /// <summary>
    /// Reads a present result. A swapchain left to the compositor to rotate stays suboptimal for as long as the device
    /// is turned, which is expected and must not rebuild it every frame.
    /// </summary>
    /// <param name="result">The result of presenting.</param>
    /// <param name="compositorRotates">Whether the compositor turns this swapchain's images.</param>
    /// <returns>The result the surface lifecycle should act on.</returns>
    internal static Result PresentResult(Result result, bool compositorRotates) =>
        compositorRotates && result == Result.SuboptimalKhr ? Result.Success : result;

    Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return UprightExtent(capabilities);
        }
        else
        {
            var framebufferSize = windowExtent;

            Extent2D actualExtent =
                new() { Width = framebufferSize.Width, Height = framebufferSize.Height };

            actualExtent.Width = Math.Clamp(
                actualExtent.Width,
                capabilities.MinImageExtent.Width,
                capabilities.MaxImageExtent.Width
            );
            actualExtent.Height = Math.Clamp(
                actualExtent.Height,
                capabilities.MinImageExtent.Height,
                capabilities.MaxImageExtent.Height
            );

            return actualExtent;
        }
    }
}
