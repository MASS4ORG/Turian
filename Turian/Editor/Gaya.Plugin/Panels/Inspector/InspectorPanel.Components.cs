namespace Gaya.Plugin.Turian;

/// <summary>
/// Component value clipboard: copy a component, paste its values onto another component of the same type, or paste
/// it as a new component. The clipboard text is the same JSON envelope the scene tree uses for nodes, so a
/// component copied here can be pasted in another Studio window.
/// </summary>
sealed partial class InspectorPanel
{
    IClipboard? systemClipboard;

    void CacheClipboard(Gui gui)
    {
        if (systemClipboard is null && gui.Platform.TryGet<IClipboard>(out var clipboard))
        {
            systemClipboard = clipboard;
        }
    }

    /// <summary>Writes <paramref name="component"/>'s values to the system clipboard.</summary>
    void CopyComponent(Component component)
    {
        systemClipboard?.SetClipboardText(NodeClipboard.WriteComponent(component));
    }

    /// <summary>Applies the copied values onto <paramref name="target"/> as one undoable step.</summary>
    void PasteComponentValues(Component target, Component? source)
    {
        if (source is null || source.GetType() != target.GetType())
        {
            return;
        }

        undo.BeginGesture();
        try
        {
            undo.RecordObject(target, "Paste Component Values");
            if (inspector.ApplyComponentValues(target, source))
            {
                overrides.Invalidate();
            }
        }
        finally { undo.EndGesture(); }
    }

    /// <summary>Adds the copied component to the selected node as one undoable step.</summary>
    void PasteComponentAsNew(Component? source)
    {
        if (source is null)
        {
            return;
        }

        undo.BeginGesture();
        try
        {
            if (inspector.SelectedNode is { } node)
            {
                undo.RecordObject(node, "Paste Component as New");
            }

            if (inspector.PasteComponentAsNew(source) is not null)
            {
                overrides.Invalidate();
            }
        }
        finally { undo.EndGesture(); }
    }

    Component? ReadClipboardComponent() =>
        NodeClipboard.TryReadComponent(systemClipboard?.GetClipboardText(), loader: null, out var component)
            ? component
            : null;
}
