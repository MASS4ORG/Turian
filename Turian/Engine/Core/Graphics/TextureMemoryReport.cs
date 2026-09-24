namespace Turian.Engine.Core;

/// <summary>
/// Reports how much device memory the loaded textures hold, and warns when a scene's textures
/// alone crowd out the rest of the frame. Called once per frame by the render system; it only
/// logs when the total has moved, so a settled scene reports a single line at load.
/// </summary>
public static class TextureMemoryReport
{
    /// <summary>
    /// Fraction of device-local memory the textures may occupy before a warning is issued.
    /// The rest of the budget goes to geometry, render targets and the driver's own allocations.
    /// </summary>
    const double warnFraction = 0.7;

    const double bytesPerMebibyte = 1024 * 1024;

    static ulong lastReportedBytes;
    static bool warned;

    /// <summary>
    /// Logs the texture memory total if it changed since the last report, warning when it exceeds
    /// the share of device-local memory textures should keep to.
    /// </summary>
    /// <param name="device">The device whose memory heaps bound the budget.</param>
    public static void ReportIfChanged(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var bytes = TextureAsset.UploadedBytes;
        if (bytes == lastReportedBytes)
        {
            return;
        }

        lastReportedBytes = bytes;

        var budget = device.DeviceLocalMemoryBytes;
        Log.Logger.LogInformation(
            "Texture memory: {Megabytes:F1} MiB across {Count} textures ({Percent:F1}% of {BudgetMegabytes:F0} MiB device-local)",
            bytes / bytesPerMebibyte,
            TextureAsset.UploadedCount,
            budget == 0 ? 0 : bytes * 100.0 / budget,
            budget / bytesPerMebibyte);

        if (budget == 0 || bytes <= budget * warnFraction)
        {
            warned = false;
            return;
        }

        if (warned)
        {
            return;
        }

        warned = true;
        Log.Logger.LogWarning(
            "Textures occupy {Megabytes:F1} MiB of {BudgetMegabytes:F0} MiB device-local memory. "
            + "Lower TextureMaxResolution in the project settings, or per texture, to cut this without recompressing",
            bytes / bytesPerMebibyte,
            budget / bytesPerMebibyte);
    }

    /// <summary>
    /// Forgets the last reported total, so the next call reports again. Call this when the texture
    /// cache is cleared.
    /// </summary>
    public static void Reset()
    {
        lastReportedBytes = 0;
        warned = false;
    }
}
