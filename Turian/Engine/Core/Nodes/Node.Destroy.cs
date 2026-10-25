namespace Turian.Engine.Core;

public partial class Node
{
    // Destructions requested in this hierarchy, held by its root in request order until the game loop flushes them.
    List<Node>? destroyQueue;
    bool compactQueued;

    /// <summary>Whether <see cref="Destroy"/> or <see cref="DestroyImmediate"/> was requested for this node.</summary>
    [JsonIgnore, Hide]
    public bool DestroyQueued { get; private set; }

    /// <summary>
    /// Destroys this node and its subtree at the next boundary of the running game loop: the end of the fixed tick when
    /// requested from a fixed update, otherwise the end of the frame. Until then the node stays in place and keeps
    /// updating; siblings destroyed together leave their parent in one pass.
    /// </summary>
    public void Destroy()
    {
        if (IsDestroyed || DestroyQueued)
        {
            return;
        }

        DestroyQueued = true;
        (HierarchyRoot().destroyQueue ??= []).Add(this);
    }

    /// <summary>
    /// Destroys this node and its subtree now and removes it from its parent, for editor and tool code outside the game
    /// loop. Removing one child searches its siblings, so the game loop should prefer <see cref="Destroy"/>.
    /// </summary>
    public void DestroyImmediate()
    {
        if (IsDestroyed)
        {
            return;
        }

        DestroyQueued = true;
        Teardown();
        Parent?.Children.Remove(this);
    }

    /// <summary>Disables and destroys this node, its components and its subtree, as unloading a scene does.</summary>
    internal void Teardown()
    {
        OnDisable();
        foreach (var component in Components.ToList())
        {
            component.Detach();
            if (component is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        // A child destroyed earlier in the same flush is still listed until its parent is compacted.
        foreach (var child in Children.ToList())
        {
            if (!child.IsDestroyed)
            {
                child.Teardown();
            }
        }

        OnDestroy();
        IsDestroyed = true;
    }

    /// <summary>
    /// Runs this hierarchy's queued destructions in request order, then removes the destroyed nodes from each affected
    /// parent once. Destructions requested by the callbacks join the same flush.
    /// </summary>
    internal void FlushDestroyQueue()
    {
        var queue = destroyQueue;
        if (queue is null || queue.Count == 0)
        {
            return;
        }

        List<Node>? parents = null;
        for (var i = 0; i < queue.Count; i++)
        {
            var node = queue[i];
            if (node.IsDestroyed)
            {
                continue;
            }

            try
            {
                node.Teardown();
            }
            catch (Exception ex)
            {
                Log.Logger.LogError(ex, "Destroying {Node} threw", node.Name);
                node.IsDestroyed = true;
            }

            if (node.Parent is { compactQueued: false } parent)
            {
                parent.compactQueued = true;
                (parents ??= []).Add(parent);
            }
        }

        queue.Clear();
        if (parents is null)
        {
            return;
        }

        var removed = new List<Node>();
        foreach (var parent in parents)
        {
            parent.compactQueued = false;
            parent.RemoveDestroyedChildren(removed);
            removed.Clear();
        }
    }

    // A root that gains a parent hands its pending destructions to the new hierarchy's root.
    void MoveDestroyQueue(Node parent)
    {
        if (destroyQueue is { Count: > 0 } pending)
        {
            (parent.HierarchyRoot().destroyQueue ??= []).AddRange(pending);
            pending.Clear();
        }
    }

    void RemoveDestroyedChildren(List<Node> removed)
    {
        if (Children is NodeCollection children)
        {
            children.RemoveDestroyed(removed);
            Release(removed);
            return;
        }

        for (var i = Children.Count - 1; i >= 0; i--)
        {
            if (Children[i] is { IsDestroyed: true, DestroyQueued: true })
            {
                Children.RemoveAt(i);
            }
        }
    }

    Node HierarchyRoot()
    {
        var root = this;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        return root;
    }
}
