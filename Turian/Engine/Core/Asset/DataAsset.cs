namespace Turian.Engine.Core;

/// <summary>
/// Represents the serialized payload stored inside a data-asset file.
/// This is the equivalent of a Unity ScriptableObject-like object.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000004")]
public class DataAsset : IdClass
{
    /// <summary>
    /// Loads a data-asset payload from the specified absolute path.
    /// </summary>
    /// <param name="absolutePath">The absolute file path of the payload asset.</param>
    /// <returns>The deserialized <see cref="DataAsset"/> payload instance.</returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the specified payload file does not exist.
    /// </exception>
    public static DataAsset? LoadContent(string absolutePath)
    {
        if (File.Exists(absolutePath))
        {
            return Serializer.Load<DataAsset>(absolutePath);
        }

        throw new FileNotFoundException($"DataAsset load failed {absolutePath}");
    }

    /// <summary>
    /// Creates an independent copy of a payload with a new id, for per-instance runtime data built
    /// from a shared template (e.g. one enemy's stats). Changes to the copy never reach the original.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="original">The payload to copy.</param>
    /// <returns>The copy.</returns>
    public static T Instantiate<T>(T original)
        where T : DataAsset
    {
        ArgumentNullException.ThrowIfNull(original);

        var copy = Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(original)) as T
            ?? throw new InvalidOperationException($"Could not copy {original.GetType().Name}");
        copy.Id = Guid.NewGuid();
        return copy;
    }
}
