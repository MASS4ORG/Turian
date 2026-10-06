namespace Turian.Editor.Core;

/// <summary>Applies hierarchy operations to selected roots as one undoable gesture.</summary>
public sealed class NodeSelectionOperations(SceneTreeController sceneTree, NodeInspectorController inspector,
    UndoService undo)
{
    /// <summary>Deletes selected subtrees once, excluding the scene root.</summary>
    public void Delete(IEnumerable<Node> selection)
    {
        var nodes = SelectionService.TopLevelNodes(selection).Where(node => node.Parent is not null).ToArray();
        Run("Delete", nodes.Select(node => node.Parent!), () =>
        {
            foreach (var node in nodes) sceneTree.DetachNode(node);
        });
        if (nodes.Length > 0) inspector.ClearSelection();
    }

    /// <summary>Duplicates selected subtrees beside their originals with available sibling names.</summary>
    public IReadOnlyList<Node> Duplicate(IEnumerable<Node> selection)
    {
        var nodes = SelectionService.TopLevelNodes(selection).Where(node => node.Parent is not null).ToArray();
        var clones = sceneTree.DuplicateNodes(nodes);
        Run("Duplicate", nodes.Select(node => node.Parent!), () =>
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                var parent = node.Parent!;
                var name = NodeNaming.GetNextAvailable(node.Name, parent.Children.Select(child => child.Name));
                var clone = clones[i];
                clone.Name = name;
                sceneTree.AttachNode(clone, parent, parent.Children.IndexOf(node) + 1);
                clone.Awake(parent);
            }
        });
        return clones;
    }

    /// <summary>Moves selected roots beneath a target, refusing cycles and preserving their world transforms.</summary>
    public void Reparent(IEnumerable<Node> selection, Node target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var nodes = SelectionService.TopLevelNodes(selection).Where(node => node.Parent is not null).ToArray();
        if (nodes.Any(node => IsAncestor(node, target))) return;
        Run("Reparent", nodes.Select(node => node.Parent!).Append(target).Concat(nodes), () =>
        {
            foreach (var node in nodes)
            {
                var world = node.GlobalTransform;
                sceneTree.DetachNode(node);
                sceneTree.AttachNode(node, target, target.Children.Count);
                node.GlobalTransform = world;
            }
        });
    }

    void Run(string label, IEnumerable<Node> owners, Action change)
    {
        var targets = owners.Distinct().ToArray();
        if (targets.Length == 0) return;
        undo.BeginGesture();
        try
        {
            foreach (var owner in targets) undo.RecordObject(owner, label);
            change();
            sceneTree.MarkAssetModified();
        }
        finally { undo.EndGesture(); }
    }

    static bool IsAncestor(Node ancestor, Node node)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
}
