namespace Gaya.Plugin.Turian;

/// <summary>
/// Changing which components the selected nodes carry: adding them from the menu or a dropped script, removing and
/// reordering them, each as one undoable step.
/// </summary>
sealed partial class InspectorPanel
{
    // The open scene takes no edits, so its nodes are shown without anything that changes them.
    bool ShowsReadOnlyScene => assets.ActiveAsset is { } scene && assets.IsReadOnly(scene.Id);

    bool CanDropScript(ScriptDragPayload drop) =>
        !ShowsReadOnlyScene && frameNodes.Count > 0
        && frameNodes.All(node => ComponentRegistry.CanAddTo(node, drop.ComponentType));

    void DropScript(ScriptDragPayload drop) => AddComponent(drop.ComponentType);

    void AddComponent(Type type)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes)
            {
                undo.RecordObject(node, "Add Component");
            }

            inspector.AddComponents(frameNodes, type);
        }
        finally { undo.EndGesture(); }
    }

    void RemoveComponents(Component representative)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes)
            {
                undo.RecordObject(node, "Remove Component");
            }

            inspector.RemoveComponents(frameNodes, representative);
        }
        finally { undo.EndGesture(); }
    }

    void MoveComponent(Component component, bool up)
    {
        undo.BeginGesture();
        try
        {
            foreach (var node in frameNodes)
            {
                undo.RecordObject(node, up ? "Move Component Up" : "Move Component Down");
            }

            inspector.MoveComponents(frameNodes, component, up);
        }
        finally { undo.EndGesture(); }
    }

    void RenderAddComponent(Gui gui)
    {
        using (gui.Node(-1, Theme.Scale(26f), "inspector/addComponent").ExpandWidth()
                   .Padding(24, 3).AlignContentY(0.5f).Enter())
        {
            var interactable = gui.GetInteractable();
            var hot = interactable.OnHover();

            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawBackgroundRect(hot ? Theme.Border : Theme.Panel, 3);
                addAnchor = gui.CurrentNode.Rect;
            }

            gui.DrawText("Add Component", Theme.Text(12), Theme.Ink);

            if (gui.Pass == Pass.Pass2Render && hot && interactable.OnClick())
            {
                addMenuAt = new Vector2(addAnchor.X, addAnchor.Y + addAnchor.H);
                addOpen = !addOpen;
            }
        }
    }

    /// <summary>
    /// The component menu, nested the way each type's <c>[ComponentContextMenu]</c> path describes it.
    /// Drawn at the top level of the panel rather than under the button: a menu opened from inside the
    /// inspector's scrolled subtree is clipped by it.
    /// </summary>
    void DrawAddComponentMenu(Gui gui) =>
        gui.CascadeMenu(ref addOpen, addMenuAt, BuildAddComponentMenu);

    void BuildAddComponentMenu(FlyoutBuilder menu) =>
        BuildComponentLevel(menu, [.. inspector.GetAvailableComponents(targets: frameNodes)], depth: 0);

    /// <summary>
    /// Emits one level of the component menu: types whose path ends here become items, and the rest are
    /// gathered by their next path segment into a submenu that recurses.
    /// </summary>
    void BuildComponentLevel(FlyoutBuilder menu, IReadOnlyList<ComponentTypeDescriptor> candidates,
        int depth)
    {
        foreach (var candidate in candidates.Where(c => Segments(c).Length == depth + 1)
                     .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var type = candidate.ComponentType;
            menu.Item(candidate.DisplayName, () => AddComponent(type));
        }

        foreach (var group in candidates.Where(c => Segments(c).Length > depth + 1)
                     .GroupBy(c => Segments(c)[depth], StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var nested = group.ToList();
            menu.Submenu(group.Key, sub => BuildComponentLevel(sub, nested, depth + 1));
        }
    }

    static string[] Segments(ComponentTypeDescriptor candidate) =>
        candidate.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
