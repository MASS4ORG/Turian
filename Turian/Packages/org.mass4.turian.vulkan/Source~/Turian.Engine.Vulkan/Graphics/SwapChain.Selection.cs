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

    Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
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
