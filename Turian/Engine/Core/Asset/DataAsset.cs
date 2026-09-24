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
    /// Creates an independent copy of this payload with a new <see cref="IdClass.Id"/>, by
    /// round-tripping it through the polymorphic JSON serializer — the same technique
    /// <c>NodeCloner.DeepClone</c> uses for scenes.
    /// </summary>
    /// <remarks>
    /// Use this for per-instance data spawned from a shared template — per-enemy stats copied from
    /// a <c>DataAsset</c>, for example. The clone shares nothing with the source: mutating one never
    /// affects the other, unlike the singleton every plain <c>LoadDataAsync</c> reader sees.
    /// </remarks>
    /// <returns>A new instance of the same concrete type, with the same field values and a fresh id.</returns>
    public DataAsset Instantiate()
    {
        var json = Serializer.Serialize(this);
        var clone = Serializer.LoadData<DataAsset>(json)
            ?? throw new InvalidOperationException($"Instantiating {GetType().Name} produced no instance.");
        clone.Id = Guid.NewGuid();
        return clone;
    }
}
