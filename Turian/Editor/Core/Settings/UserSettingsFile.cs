using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>
/// Reads a settings page from the user's settings file, the one Studio writes, so tools that run without Studio
/// follow the same choices.
/// </summary>
public static class UserSettingsFile
{
    /// <summary>The user's settings file: <c>settings.json</c> in the per-user folder.</summary>
    public static string DefaultPath => Path.Combine(GayaConfig.Directory, "settings.json");

    /// <summary>
    /// Fills a page's public settable properties from its section of the file, leaving the others, and the whole page
    /// when the file, its section or the page's <see cref="Gaya.EditorSettingAttribute"/> is missing or unreadable.
    /// </summary>
    /// <param name="page">An object carrying <see cref="Gaya.EditorSettingAttribute"/>.</param>
    /// <param name="path">The settings file; the user's own when null.</param>
    /// <returns>The same page.</returns>
    public static T Load<T>(T page, string? path = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(page);
        var type = page.GetType();
        if (type.GetCustomAttribute<Gaya.EditorSettingAttribute>() is not { } attribute || !File.Exists(path ??= DefaultPath))
        {
            return page;
        }

        JsonObject? section;
        try
        {
            section = JsonNode.Parse(File.ReadAllText(path))?[attribute.IdFor(type)] as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            return page;
        }

        if (section is null)
        {
            return page;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(static property => property is { CanRead: true, CanWrite: true }))
        {
            if (section[property.Name] is not { } value)
            {
                continue;
            }

            try
            {
                property.SetValue(page, value.Deserialize(property.PropertyType));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException
                                                  or ArgumentException)
            {
                // A value of the wrong kind keeps the page's own, as Studio does.
            }
        }

        return page;
    }
}
