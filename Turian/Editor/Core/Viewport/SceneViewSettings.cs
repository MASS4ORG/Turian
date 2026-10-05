namespace Turian.Editor.Core;

/// <summary>Controls the environment shown by the editor's Scene view.</summary>
[EditorSetting("Scene Viewer")]
public sealed class SceneViewSettings
{
    /// <summary>The rendering layers shown by the Scene view.</summary>
    public LayerMask VisibleLayers { get; set; } = LayerMask.Everything;

    /// <summary>The rendering layers excluded from selection, picking and transform interaction.</summary>
    public LayerMask LockedLayers
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            LocksChanged?.Invoke();
        }
    }

    /// <summary>Raised when layer locks change, so existing selections can be cleared.</summary>
    public event Action? LocksChanged;

    /// <summary>Whether a node permits selection and transform interaction.</summary>
    public bool CanSelect(Node node) => !node.IsDestroyed && !LockedLayers.Contains(node.RenderLayer);

    /// <summary>Changes the visibility of one rendering layer.</summary>
    public void SetVisible(int index, bool visible)
    {
        var bit = LayerMask.FromLayer(index);
        VisibleLayers = visible ? VisibleLayers | bit : VisibleLayers & ~bit;
    }

    /// <summary>Changes the interaction lock of one rendering layer.</summary>
    public void SetLocked(int index, bool locked)
    {
        var bit = LayerMask.FromLayer(index);
        LockedLayers = locked ? LockedLayers | bit : LockedLayers & ~bit;
    }

    /// <summary>Gets or sets normalized linear RGB channels for the empty Scene background.</summary>
    [EditorSetting("Empty Sky Color", Description = "Background color where no scene geometry is drawn.")]
    public Vector3 EmptySkyColor { get; set; } = new(0.035f);
}
