namespace Turian.Engine.Core;

/// <summary>Handles a change to a <see cref="DataAsset"/>.</summary>
/// <param name="asset">The asset that changed.</param>
/// <param name="member">The member that changed, or an empty string when the whole asset was reloaded.</param>
public delegate void DataAssetChangedHandler(DataAsset asset, string member);

/// <summary>
/// Represents the serialized payload stored inside a data-asset file.
/// The payload is shared by the asset loader and can be copied with Instantiate.
/// </summary>
[TypeId("2072d8c2-86ad-52b9-9f91-42695ff0800d")]
public class DataAsset : IdObject
{
    /// <summary>The current base payload schema, written into every newly saved DataAsset.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The payload schema version; missing versions are migrated as version zero when loading content.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [field: NonSerialized]
    internal DataAssetSession? RuntimeSession { get; set; }

    /// <summary>Whether this payload belongs to a running Play/export session.</summary>
    [JsonIgnore]
    public bool IsRuntime => RuntimeSession is not null;

    internal virtual void InitializeRuntime(DataAssetSession session) { }

    internal virtual void ResetRuntime() => Changed = null;

    /// <summary>The runtime session required for gameplay operations; authored assets cannot hold gameplay state.</summary>
    protected DataAssetSession RequireRuntimeSession() => RuntimeSession ??
        throw new InvalidOperationException("This DataAsset has not been hydrated into a running session.");

    /// <summary>Migrates an older payload before runtime hydration; overrides must call the base implementation.</summary>
    protected virtual void Migrate(int sourceVersion)
    {
        if (sourceVersion < 0 || sourceVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported DataAsset schema version {sourceVersion}.");
        }
        SchemaVersion = CurrentSchemaVersion;
    }

    internal static DataAsset? DeserializePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.TryGetProperty(nameof(SchemaVersion), out var field)
            ? field.GetInt32() : 0;
        var asset = Serializer.LoadData<DataAsset>(json);
        asset?.Migrate(version);
        return asset;
    }
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
    /// <param name="database">The asset database a variant's bases are read from, or null when there is none.</param>
    /// <returns>The deserialized <see cref="DataAsset"/> payload instance.</returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the specified payload file does not exist.
    /// </exception>
    public static DataAsset? LoadContent(string absolutePath, AssetDatabase? database)
        => LoadContent(absolutePath, null, id => DataAssetVariants.ReadFromDatabase(database, id));

    internal static DataAsset? LoadContent(string absolutePath, Guid? expectedAssetId,
        Func<Guid, string?> readBase)
    {
        if (File.Exists(absolutePath))
        {
            return DeserializePayload(
                DataAssetVariants.Flatten(File.ReadAllText(absolutePath), readBase, expectedAssetId));
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
