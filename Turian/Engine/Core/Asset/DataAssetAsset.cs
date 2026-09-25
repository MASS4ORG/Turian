namespace Turian.Engine.Core;

/// <summary>
/// Asset metadata that points to a serialized <see cref="DataAsset"/> payload file.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000006")]
public class DataAssetAsset : Asset
{
    readonly Lock contentGate = new();
    DataAsset? content;

    /// <summary>
    /// Gets the payload associated with this metadata asset. It is read once and then shared: every later
    /// call returns the same instance without I/O, so a change made through one holder is seen by all.
    /// </summary>
    /// <remarks>
    /// <see cref="IAssetLoader"/> keeps one metadata instance per id, so every caller loading through the
    /// same loader shares one payload. Use <see cref="DataAsset.Instantiate{T}"/> for a private copy.
    /// </remarks>
    /// <param name="projectPath">The absolute project root path.</param>
    /// <returns>The shared <see cref="DataAsset"/> payload instance.</returns>
    public DataAsset? GetContent(string projectPath)
    {
        lock (contentGate)
        {
            return content ??= ReadContent(projectPath);
        }
    }

    /// <summary>
    /// Re-reads the payload into the instance <see cref="GetContent"/> already handed out, so live
    /// references see the new values. The instance is replaced only when the payload type changed.
    /// </summary>
    /// <param name="projectPath">The absolute project root path.</param>
    /// <returns>The shared <see cref="DataAsset"/> payload instance.</returns>
    public DataAsset? Reload(string projectPath)
    {
        lock (contentGate)
        {
            var fresh = ReadContent(projectPath);
            if (content is null || fresh is null || fresh.GetType() != content.GetType())
            {
                return content = fresh;
            }

            CopyFields(fresh, content);
            return content;
        }
    }

    /// <summary>
    /// Loads a payload from the specified absolute path.
    /// </summary>
    /// <param name="absolutePath">The absolute file path of the payload asset.</param>
    /// <returns>The deserialized <see cref="DataAsset"/> payload instance.</returns>
    public static DataAsset? LoadContent(string absolutePath)
    {
        return DataAsset.LoadContent(absolutePath);
    }

    DataAsset? ReadContent(string projectPath)
    {
        var sourcePath = Path.Combine(projectPath, RelativePath);
        if (File.Exists(sourcePath)
            || !AssetDatabase.TryGetInstance(out var database)
            || database is null
            || !database.TryGetAssetProvider(Id, out var provider)
            || provider is null)
        {
            return DataAsset.LoadContent(sourcePath);
        }

        // A built or play-mode game has no sources, only the imported copy the catalog points at.
        using var reader = new StreamReader(provider.GetAssetStream());
        return Serializer.LoadData<DataAsset>(reader.ReadToEnd());
    }

    static void CopyFields(DataAsset source, DataAsset target)
    {
        for (var type = source.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.DeclaredOnly))
            {
                field.SetValue(target, field.GetValue(source));
            }
        }
    }
}
