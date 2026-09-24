namespace Turian.Editor.Core;

/// <summary>
/// The editor surface a play session needs: where to copy the scene from, and where to point the
/// panels while the copy is running. <see cref="SceneTreeController"/> implements it for
/// the Studio; a headless harness can implement it in a few lines, which is what makes play mode
/// testable outside a window.
/// </summary>
public interface IPlaySceneHost
{
    /// <summary>Root of the scene a session should copy, or <c>null</c> when nothing is open.</summary>
    Node? CurrentSceneRoot { get; }

    /// <summary>The asset the open scene came from, used as the running scene's id.</summary>
    Asset? CurrentAsset { get; }

    /// <summary>Points the editor's panels at the running copy of the scene.</summary>
    /// <param name="root">Root of the running hierarchy.</param>
    void ShowRuntimeScene(Node root);

    /// <summary>Points the editor's panels back at the edited scene when the session ends.</summary>
    void ShowEditorScene();
}
