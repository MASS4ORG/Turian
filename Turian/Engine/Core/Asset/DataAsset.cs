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
}
