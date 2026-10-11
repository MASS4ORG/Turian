namespace Gaya.Packages.Editor;

/// <summary>Shows the Bricks panel's selection, the selected brick or registry, in the host's inspector.</summary>
public interface IBrickInspector
{
    /// <summary>Shows what <see cref="BricksController.Selected"/> or <see cref="BricksController.SelectedRegistry"/> names.</summary>
    /// <param name="controller">The panel's controller.</param>
    void ShowSelection(BricksController controller);

    /// <summary>Shows the active workspace's brick settings.</summary>
    /// <param name="controller">The panel's controller.</param>
    void ShowSettings(BricksController controller);

    /// <summary>Brings what the inspector shows in line after the controller's lists were rebuilt.</summary>
    /// <param name="controller">The panel's controller.</param>
    void Refresh(BricksController controller);
}

/// <summary>Asks the user for a folder or a <c>.brick</c> file to add a brick from.</summary>
public interface IBrickFileDialogs
{
    /// <summary>Shows a picker and reports the chosen path, or <c>null</c> when cancelled.</summary>
    /// <param name="folder">Whether a folder rather than a file is picked.</param>
    /// <param name="title">The dialog title.</param>
    /// <param name="onComplete">Receives the path.</param>
    void Pick(bool folder, string title, Action<string?> onComplete);
}
