namespace Turian.Engine.Core;

/// <summary>Handles a change to a <see cref="DataAsset"/>.</summary>
/// <param name="asset">The asset that changed.</param>
/// <param name="member">The member that changed, or an empty string when the whole asset was reloaded.</param>
public delegate void DataAssetChangedHandler(DataAsset asset, string member);

/// <summary>
/// Represents the serialized payload stored inside a data-asset file.
/// The payload is shared by the asset loader and can be copied with Instantiate.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000004")]
public class DataAsset : IdClass
{
    /// <summary>
    /// Raised when a member changes: by an <see cref="ObservableAttribute"/> property, a call to
    /// <see cref="NotifyChanged"/>, or a reload of the asset (with an empty member name).
    /// </summary>
    public event DataAssetChangedHandler? Changed;

    /// <summary>Raises <see cref="Changed"/> for <paramref name="member"/>.</summary>
    /// <param name="member">The member that changed, or an empty string for the whole asset.</param>
    public void NotifyChanged(string member) => Changed?.Invoke(this, member);

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
