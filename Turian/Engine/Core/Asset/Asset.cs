namespace Turian.Engine.Core;

/// <summary>
/// Represents an asset with a file path.
/// </summary>
[TypeId("a3000000-0000-4000-8000-000000000003")]
public class Asset : IdClass
{
    string relativePath = string.Empty;

    /// <summary>
    /// Raised whenever editor-only runtime metadata changes for this asset.
    /// </summary>
    public event Action<Asset>? StateChanged;

    /// <summary>
    /// Gets or sets the file path of the asset.
    /// </summary>
    [HideInEditor]
    public string RelativePath
    {
        get => relativePath;
        set
        {
            if (string.Equals(relativePath, value, StringComparison.Ordinal))
            {
                return;
            }

            relativePath = value;
            OnStateChanged();
        }
    }

    /// <summary>
    /// Signal that the Asset was modified
    /// </summary>
    [JsonIgnore]
    public bool IsModified { get; private set; }

    /// <summary>
    /// Gets the asset display name derived from its relative path.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => Path.GetFileNameWithoutExtension(relativePath);

    /// <summary>
    /// Marks the asset as modified.
    /// </summary>
    public void MarkModified()
    {
        if (IsModified)
        {
            return;
        }

        IsModified = true;
        OnStateChanged();
    }

    /// <summary>
    /// Marks the asset as saved.
    /// </summary>
    public void MarkSaved()
    {
        if (!IsModified)
        {
            return;
        }

        IsModified = false;
        OnStateChanged();
    }

    /// <summary>
    /// Updates the relative path while preserving editor runtime state.
    /// </summary>
    /// <param name="newRelativePath"></param>
    public void RenameTo(string newRelativePath)
    {
        RelativePath = newRelativePath;
    }

    /// <summary>
    /// Notifies listeners that editor runtime state changed.
    /// </summary>
    protected void OnStateChanged()
    {
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Loads the content from the specified absolute path.
    /// </summary>
    /// <param name="absolutePath">The absolute path to load content from.</param>
    /// <returns>An instance of <see cref="Asset"/> if the content is successfully loaded; otherwise, an exception is thrown.</returns>
    /// <exception cref="Exception">Thrown when the content fails to load from the given path.</exception>
    public static Asset? Load(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("Node load failed");
        }

        return Serializer.Load<Asset>(absolutePath);
    }
}
