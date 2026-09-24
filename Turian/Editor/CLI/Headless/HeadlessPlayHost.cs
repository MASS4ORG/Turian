namespace Turian.Editor.CLI;

/// <summary>
/// The minimum <see cref="IPlaySceneHost"/> a play session needs outside the Studio: it holds the
/// scene to copy and remembers which hierarchy the session swapped to, without any panels.
/// </summary>
/// <param name="sceneRoot">Root of the scene the session should copy.</param>
/// <param name="sceneAsset">The asset the scene came from, or <c>null</c>.</param>
sealed class HeadlessPlayHost(Node sceneRoot, Asset? sceneAsset) : IPlaySceneHost
{
    /// <inheritdoc/>
    public Node CurrentSceneRoot => RuntimeRoot ?? sceneRoot;

    /// <inheritdoc/>
    public Asset? CurrentAsset => sceneAsset;

    /// <summary>The running copy the session adopted, or <c>null</c> when stopped.</summary>
    public Node? RuntimeRoot { get; private set; }

    /// <inheritdoc/>
    public void ShowRuntimeScene(Node root) => RuntimeRoot = root;

    /// <inheritdoc/>
    public void ShowEditorScene() => RuntimeRoot = null;
}
