namespace Turian.Engine.Core;

/// <summary>
/// Represents a loaded scene instance containing its root node, metadata,
/// and runtime lifecycle state.
/// </summary>
[PublicAPI]
public sealed class LoadedScene
{
    /// <summary>
    /// Gets the unique identifier of the scene asset.
    /// </summary>
    public Guid AssetId { get; internal set; }

    /// <summary>
    /// Gets the root node of the scene hierarchy.
    /// </summary>
    public Node RootNode { get; internal set; }

    /// <summary>
    /// Gets or sets the display name of the scene.
    /// </summary>
    public string Name { get; internal set; } = "New Scene";

    /// <summary>
    /// Gets a value indicating whether this scene has already run its deferred start phase.
    /// </summary>
    public bool HasStarted { get; private set; }

    /// <summary>
    /// Gets the UTC timestamp when this scene was loaded.
    /// </summary>
    public DateTimeOffset LoadedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoadedScene"/> class.
    /// </summary>
    /// <param name="assetId">The unique identifier of the scene asset.</param>
    /// <param name="rootNode">The root node of the scene hierarchy.</param>
    internal LoadedScene(Guid assetId, Node rootNode)
    {
        ArgumentNullException.ThrowIfNull(rootNode);

        AssetId = assetId;
        RootNode = rootNode;
    }

    /// <summary>
    /// Marks this scene as having completed its deferred start phase.
    /// </summary>
    internal void MarkStarted()
    {
        HasStarted = true;
    }

    /// <summary>
    /// Resets transient runtime state for this loaded scene.
    /// </summary>
    internal void ResetRuntimeState()
    {
        HasStarted = false;
    }
}
