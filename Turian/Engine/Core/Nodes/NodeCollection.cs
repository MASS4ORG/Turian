using System.Collections.Specialized;
using System.ComponentModel;

namespace Turian.Engine.Core;

/// <summary>A node's children, which can drop every flushed destruction in one pass with one reset notification.</summary>
sealed class NodeCollection : ObservableCollection<Node>
{
    /// <summary>Creates an empty collection.</summary>
    public NodeCollection()
    {
    }

    /// <summary>Creates a collection holding <paramref name="nodes"/> in order.</summary>
    public NodeCollection(IEnumerable<Node> nodes) : base(nodes)
    {
    }

    /// <summary>
    /// Whether the reset being raised comes from <see cref="RemoveDestroyed"/>, whose owner has the removed nodes and
    /// so needs no resynchronization.
    /// </summary>
    internal bool Compacting { get; private set; }

    /// <summary>
    /// Removes every child whose queued destruction has run, keeping the others in order, and appends the removed ones
    /// to <paramref name="removed"/>.
    /// </summary>
    internal void RemoveDestroyed(List<Node> removed)
    {
        CheckReentrancy();
        var items = (List<Node>)Items;
        var start = removed.Count;
        var kept = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var node = items[i];
            if (node is { IsDestroyed: true, DestroyQueued: true })
            {
                removed.Add(node);
            }
            else
            {
                items[kept++] = node;
            }
        }

        if (removed.Count == start)
        {
            return;
        }

        items.RemoveRange(kept, items.Count - kept);
        Compacting = true;
        try
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        finally
        {
            Compacting = false;
        }
    }
}
