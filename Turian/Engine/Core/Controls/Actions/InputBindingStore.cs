namespace Turian.Engine.Core;

/// <summary>
/// Reads and writes a player's rebindings as a small JSON file beside the game's other user data:
/// paths keyed by <c>Map/Action/index</c>, exactly what
/// <see cref="InputActionService.CaptureOverrides"/> produces.
/// </summary>
/// <remarks>
/// The file is a convenience, never a requirement: an unreadable one, or an entry naming an action
/// the asset no longer has, is discarded and the authored binding stands.
/// </remarks>
public static class InputBindingStore
{
    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    /// <summary>The file a game stores its rebindings in, under the user's application data.</summary>
    /// <param name="productName">The folder the game keeps its user data in.</param>
    /// <returns>An absolute path.</returns>
    public static string DefaultPath(string productName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        productName, "inputbindings.json");

    /// <summary>Writes the service's current bindings.</summary>
    /// <param name="service">The service whose bindings are captured.</param>
    /// <param name="path">The file to write.</param>
    /// <returns>True when the file was written.</returns>
    public static bool Save(InputActionService service, string path)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(service.CaptureOverrides(), jsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Logger.LogWarning(ex, "Input bindings could not be saved to {Path}", path);
            return false;
        }
    }

    /// <summary>Restores stored bindings onto the service's loaded asset.</summary>
    /// <param name="service">The service to apply them to.</param>
    /// <param name="path">The file to read.</param>
    /// <returns>True when a file was read and applied.</returns>
    public static bool Load(InputActionService service, string path)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path)) return false;

            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            service.ApplyOverrides(stored);
            return stored is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Logger.LogWarning(ex, "Input bindings could not be read from {Path}", path);
            return false;
        }
    }
}
