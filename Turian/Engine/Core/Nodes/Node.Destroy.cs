namespace Turian.Engine.Core;

public partial class Node
{
    // Destructions requested in this hierarchy, held by its root in request order until the game loop flushes them.
    List<Node>? destroyQueue;
    List<NodeCollection>? destroyNotifications;

    /// <summary>Whether <see cref="Destroy"/> or <see cref="DestroyImmediate"/> was requested for this node.</summary>
    [JsonIgnore, Hide]
    public bool DestroyQueued { get; private set; }

    /// <summary>
    /// Destroys this node and its subtree at the next boundary of the running game loop: the end of the fixed tick when
    /// requested from a fixed update, otherwise the end of the frame. Until then the node stays in place and keeps
    /// updating; removal preserves sibling order.
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
    /// loop.
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
    /// Runs this hierarchy's queued destructions in request order and removes each node from its parent.
    /// Destructions requested by the callbacks join the same flush.
    /// </summary>
    internal void FlushDestroyQueue()
    {
        var queue = destroyQueue;
        if (queue is null || queue.Count == 0)
        {
            return;
        }

        var notifications = destroyNotifications ?? [];
        destroyNotifications = null;
        try
        {
            for (var i = 0; i < queue.Count; i++)
            {
                var node = queue[i];
                if (node.IsDestroyed) { continue; }

                DestroyQueuedNode(node, notifications);
            }

            queue.Clear();
        }
        finally
        {
            PublishDestructionNotifications(notifications);
            destroyNotifications = notifications;
        }
    }

    static void DestroyQueuedNode(Node node, List<NodeCollection> notifications)
    {
        try
        {
            node.Teardown();
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "Destroying {Node} threw", node.Name);
            node.IsDestroyed = true;
        }

        if (node.Parent is not { } parent) { return; }
        var children = parent.Children;
        if (children.BeginDestructionNotifications()) { notifications.Add(children); }
        children.Remove(node);
    }

    static void PublishDestructionNotifications(List<NodeCollection> notifications)
    {
        foreach (var children in notifications)
        {
            try
            {
                children.EndDestructionNotifications();
            }
            catch (Exception ex)
            {
                Log.Logger.LogError(ex, "Notifying destroyed children threw");
            }
        }

        notifications.Clear();
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
