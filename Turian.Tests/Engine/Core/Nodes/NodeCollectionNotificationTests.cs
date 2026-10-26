namespace Turian.Tests;

/// <summary>Coarse children notifications expose completed membership and batch deferred destruction.</summary>
public class NodeCollectionNotificationTests
{
    /// <summary>Reparent notifications see both collections and the parent pointer already committed.</summary>
    [Fact]
    public void ReparentNotificationsSeeCompletedMembership()
    {
        var source = new Node();
        var target = new Node();
        var child = new Node();
        source.Children.Add(child);
        var notifications = new List<NodeCollection>();
        void Observe(object? sender, EventArgs args)
        {
            Assert.Same(EventArgs.Empty, args);
            Assert.Same(target, child.Parent);
            Assert.Empty(source.Children);
            Assert.Equal([child], target.Children);
            notifications.Add(Assert.IsType<NodeCollection>(sender));
        }

        source.Children.StructureChanged += Observe;
        target.Children.StructureChanged += Observe;
        target.Children.Add(child);

        Assert.Equal([source.Children, target.Children], notifications);
    }

    /// <summary>Replacement coalesces moves and removals; storage-only reads and mutation no-ops stay silent.</summary>
    [Fact]
    public void ReplacementNotifiesOnceAndReadsStaySilent()
    {
        var parent = new Node();
        Node a = new(), b = new(), c = new(), added = new();
        parent.Children = [a, b, c];
        var notifications = 0;
        parent.Children.StructureChanged += (_, _) =>
        {
            notifications++;
            Assert.Equal([c, a, added], parent.Children);
            Assert.All(parent.Children, child => Assert.Same(parent, child.Parent));
            Assert.Null(b.Parent);
        };

        parent.Children.ReplaceWith([c, a, added, c]);
        Assert.Equal(1, notifications);

        var version = parent.Children.Version;
        _ = parent.Children.IndexOf(c);
        _ = parent.Children[0];
        parent.Children.Add(added);
        parent.Children.Move(0, 0);
        parent.Children[0] = c;
        parent.Children.ReplaceWith(parent.Children);
        Assert.False(parent.Children.Remove(b));
        Assert.Equal(version, parent.Children.Version);
        Assert.Equal(1, notifications);
    }

    /// <summary>Index replacement and insertion publish once even when moving an already listed child.</summary>
    [Fact]
    public void IndexedOperationsPublishCompletedLogicalOrder()
    {
        var parent = new Node();
        var nodes = Enumerable.Range(0, 8).Select(_ => new Node()).ToArray();
        parent.Children = [.. nodes];
        parent.Children.Remove(nodes[2]);
        var notifications = 0;
        parent.Children.StructureChanged += (_, _) =>
        {
            notifications++;
            var items = parent.Children.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                Assert.Same(parent, items[i].Parent);
                Assert.Equal(i, parent.Children.IndexOf(items[i]));
                Assert.Same(items[i], parent.Children[i]);
            }
        };

        parent.Children[3] = nodes[0];
        Assert.Equal([nodes[1], nodes[3], nodes[0], nodes[5], nodes[6], nodes[7]], parent.Children);
        Assert.Equal(1, notifications);
        parent.Children.Insert(1, nodes[7]);
        Assert.Equal(2, notifications);
        parent.Children.Move(1, 5);
        Assert.Equal(3, notifications);
        parent.Children.RemoveAt(1);
        Assert.Equal(4, notifications);
        parent.Children.Clear();
        Assert.Equal(5, notifications);
        parent.Children.Clear();
        Assert.Equal(5, notifications);
    }

    /// <summary>Builder replacement coalesces edits without changing owned hierarchy membership.</summary>
    [Fact]
    public void BuilderReplacementNotifiesOnce()
    {
        var parent = new Node();
        Node a = new(), b = new();
        parent.Children = [a, b];
        NodeCollection builder = [a];
        var version = parent.Children.Version;
        var notifications = 0;
        builder.StructureChanged += (_, _) => notifications++;

        builder.ReplaceWith([b, a, b]);

        Assert.Equal([b, a], builder);
        Assert.Equal(1, notifications);
        Assert.Equal(version, parent.Children.Version);
        Assert.Equal([a, b], parent.Children);
    }

    /// <summary>Observer failures do not leave future structural notifications deferred.</summary>
    [Fact]
    public void ThrowingObserverDoesNotSuppressFutureNotifications()
    {
        var parent = new Node();
        EventHandler throwing = (_, _) => throw new InvalidOperationException("observer failed");
        parent.Children.StructureChanged += throwing;
        var child = new Node();

        Assert.Throws<InvalidOperationException>(() => parent.Children.Add(child));
        Assert.Same(parent, child.Parent);
        Assert.Equal([child], parent.Children);

        parent.Children.StructureChanged -= throwing;
        var notifications = 0;
        parent.Children.StructureChanged += (_, _) => notifications++;
        parent.Children.Clear();
        Assert.Equal(1, notifications);
    }
}
