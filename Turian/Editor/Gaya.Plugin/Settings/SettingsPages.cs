namespace Gaya.Plugin.Turian;

/// <summary>
/// Turns a settings object into the page the studio registers, reading its class-level
/// <see cref="EditorSettingAttribute"/> so the class states its own category, description and scope.
/// The same attribute is what user code uses, so built-in and contributed pages are declared alike.
/// </summary>
static class SettingsPages
{
    /// <summary>Describes a settings object as a page.</summary>
    /// <param name="id">The stable id its stored values are keyed by.</param>
    /// <param name="target">The settings object.</param>
    /// <returns>The page to register.</returns>
    public static SettingsPageDescriptor Describe(string id, object target)
    {
        var attribute = target.GetType().GetCustomAttribute<EditorSettingAttribute>();

        return new SettingsPageDescriptor(id, attribute?.Path ?? target.GetType().Name, target,
            attribute?.Workspace == true ? SettingsScope.Workspace : SettingsScope.User,
            attribute?.Order ?? 0)
        {
            Description = attribute?.Description ?? "",
        };
    }
}
