namespace Turian.Engine.Core;

/// <summary>
/// Specific Exception type for Vulkan related issues.
/// </summary>
/// <remarks>
/// .ctor
/// </remarks>
/// <param name="message"></param>
/// <param name="additionalInfo"></param>
public class VulkanException(string? message = null, object? additionalInfo = null) : Exception(FormatMessage(message, additionalInfo))
{
    static string FormatMessage(string? message, object? additionalInfo)
    {
        message ??= "Vulkan: An error occurred. {0}";
        if (additionalInfo == null || additionalInfo is string additionalInfoStr && string.IsNullOrEmpty(additionalInfoStr))
        {
            return message;
        }

        var formatted = string.Format(CultureInfo.InvariantCulture, message, additionalInfo);
        Log.Logger.LogError("{Message}", formatted);

        return formatted;
    }
}
