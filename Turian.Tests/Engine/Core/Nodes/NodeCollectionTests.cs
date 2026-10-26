namespace Turian.Tests;

/// <summary>Checks ordered tombstone storage and the public hierarchy mutation contract.</summary>
[Collection(SerialTests.Name)]
public class NodeCollectionTests
{
    static Node[] Populate(Node root, int count)
    {
        var nodes = Enumerable.Range(0, count).Select(static i => new Node { Name = i.ToString() }).ToArray();
        foreach (var node in nodes) { root.Children.Add(node); }
        return nodes;
    }

    static int Storage(NodeCollection children, string name) =>
        (int)typeof(NodeCollection).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(children)!;

    /// <summary>Every indexed operation addresses live logical positions after removals.</summary>
    [Fact]
    public void IndexedOperationsUseLivePositions()
    {
        var root = new Node();
        var nodes = Populate(root, 8);
        root.Children.Remove(nodes[0]);
        root.Children.Remove(nodes[3]);
        Assert.Same(nodes[4], root.Children[2]);
        Assert.Equal(3, root.Children.IndexOf(nodes[5]));
        root.Children.Remove(nodes[2]);
        var inserted = new Node();
        root.Children.Insert(1, inserted);
        Assert.Equal([nodes[1], inserted, nodes[4], nodes[5], nodes[6], nodes[7]], root.Children);
        root.Children.Remove(nodes[5]);
        root.Children.Move(3, 0);
        Assert.Equal([nodes[6], nodes[1], inserted, nodes[4], nodes[7]], root.Children);
        root.Children.Move(0, 4);
        root.Children.RemoveAt(1);
        Assert.Null(inserted.Parent);
        root.Children.Remove(nodes[4]);
        var replacement = new Node();
        root.Children[1] = replacement;
        Assert.Null(nodes[7].Parent);
        Assert.Equal([nodes[1], replacement, nodes[6]], root.Children);
    }

    /// <summary>Clearing from either end trims slots without compaction.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndRemovalsNeverCompact(bool reverse)
    {
        var root = new Node();
        var nodes = Populate(root, 128);
        foreach (var node in reverse ? nodes.Reverse() : nodes)
        {
            Assert.True(root.Children.Remove(node));
            Assert.Equal(0, Storage(root.Children, "holes"));
            Assert.Equal(0, Storage(root.Children, "layout"));
        }

        root.Children.Add(nodes[0]);
        Assert.Equal([nodes[0]], root.Children);
        var contains = root.Children.Contains(nodes[0]);
        Assert.True(contains);
    }

    /// <summary>Indexed operations retain leading vacant space when no interior holes need compaction.</summary>
    [Fact]
    public void IndexedOperationsRetainLeadingSpace()
    {
        var root = new Node();
        var nodes = Populate(root, 6);
        root.Children.Remove(nodes[0]);
        Assert.Same(nodes[2], root.Children[1]);
        Assert.Equal(1, root.Children.IndexOf(nodes[2]));
        root.Children.Move(0, 2);
        root.Children.RemoveAt(0);
        root.Children[0] = new Node();
        root.Children.Insert(1, new Node());
        Assert.Equal(0, Storage(root.Children, "layout"));
        Assert.Equal(0, Storage(root.Children, "holes"));
        Assert.Equal(5, root.Children.Count);
    }

    /// <summary>Trimming across existing holes keeps their count accurate and preserves remaining order.</summary>
    [Fact]
    public void TrimmingAbsorbsAdjacentHoles()
    {
        var root = new Node();
        var nodes = Populate(root, 8);
        root.Children.Remove(nodes[1]);
        root.Children.Remove(nodes[2]);
        root.Children.Remove(nodes[5]);
        root.Children.Remove(nodes[6]);
        root.Children.Remove(nodes[0]);
        root.Children.Remove(nodes[7]);
        Assert.Equal(0, Storage(root.Children, "holes"));
        Assert.Equal(0, Storage(root.Children, "layout"));
        Assert.Equal([nodes[3], nodes[4]], root.Children);
        root.Children.Clear();
        root.Children.Clear();
        Assert.All(nodes, static node => Assert.Null(node.Parent));
        Assert.False(root.Children.Remove(nodes[0]));
        Assert.Equal(-1, root.Children.IndexOf(nodes[0]));
    }

    /// <summary>Appending reclaims leading and interior vacant slots before allocating a larger array.</summary>
    [Fact]
    public void AppendCompactsBeforeGrowing()
    {
        var root = new Node();
        var nodes = Populate(root, 8);
        var storage = typeof(NodeCollection).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = storage.GetValue(root.Children);
        root.Children.Remove(nodes[0]);
        root.Children.Remove(nodes[3]);
        var added = new Node();
        root.Children.Add(added);
        Assert.Same(original, storage.GetValue(root.Children));
        Assert.Equal([nodes[1], nodes[2], nodes[4], nodes[5], nodes[6], nodes[7], added], root.Children);
        Assert.All(root.Children, node => Assert.True(root.Children.IndexOf(node) >= 0));
    }

    /// <summary>Interior holes compact only beyond the threshold and preserve order and intrusive membership.</summary>
    [Theory]
    [InlineData(128, 16)]
    [InlineData(1024, 113)]
    public void CompactionThresholdPreservesOrder(int count, int threshold)
    {
        var root = new Node();
        var nodes = Populate(root, count);
        for (var i = 1; i <= threshold; i++) { root.Children.Remove(nodes[i]); }
        Assert.Equal(threshold, Storage(root.Children, "holes"));
        Assert.Equal(0, Storage(root.Children, "layout"));
        root.Children.Remove(nodes[threshold + 1]);
        Assert.Equal(0, Storage(root.Children, "holes"));
        Assert.Equal(1, Storage(root.Children, "layout"));
        Assert.Equal([nodes[0], .. nodes.Skip(threshold + 2)], root.Children);
        Assert.All(root.Children, node => Assert.True(root.Children.IndexOf(node) >= 0));
    }

    /// <summary>Indexed reads can compact during a foreach without repeating or skipping children.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EnumeratorRemapsAcrossReadCompaction(int before)
    {
        var root = new Node();
        var nodes = Populate(root, 8);
        root.Children.Remove(nodes[0]);
        root.Children.Remove(nodes[3]);
        var expected = new[] { nodes[1], nodes[2], nodes[4], nodes[5], nodes[6], nodes[7] };
        var seen = new List<Node>();
        var version = root.Children.Version;
        using var enumerator = root.Children.GetEnumerator();
        for (var i = 0; i < before; i++)
        {
            Assert.True(enumerator.MoveNext());
            seen.Add(enumerator.Current);
        }

        Assert.Equal(3, root.Children.IndexOf(nodes[5]));
        while (enumerator.MoveNext()) { seen.Add(enumerator.Current); }
        Assert.Equal(expected, seen);
        Assert.Equal(version, root.Children.Version);
        Assert.False(enumerator.MoveNext());
    }

    /// <summary>Every structural operation invalidates existing enumerators.</summary>
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("insert")]
    [InlineData("move")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("assign")]
    public void MutationInvalidatesEnumeration(string operation)
    {
        var root = new Node();
        var nodes = Populate(root, 5);
        var enumerator = root.Children.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        switch (operation)
        {
            case "add": root.Children.Add(new Node()); break;
            case "remove": root.Children.Remove(nodes[2]); break;
            case "insert": root.Children.Insert(2, new Node()); break;
            case "move": root.Children.Move(1, 3); break;
            case "replace": root.Children[2] = new Node(); break;
            case "clear": root.Children.Clear(); break;
            case "assign": root.Children = [new Node()]; break;
        }

        Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
    }

    /// <summary>Parent assignment, repeated adds and inserts move a single listing and maintain scene state.</summary>
    [Fact]
    public void ParentAssignmentAndExistingChildrenMove()
    {
        var first = new Node { Position = Vector3.UnitX };
        var second = new Node { IsActive = false, Position = Vector3.UnitY };
        var nodes = Populate(first, 4);
        first.Awake(null);
        second.Awake(null);
        var child = nodes[0];
        Assert.Equal(Vector3.UnitX, child.GlobalTransform.Position);
        first.Children.Add(child);
        Assert.Equal([nodes[1], nodes[2], nodes[3], child], first.Children);
        first.Children.Insert(1, child);
        Assert.Equal([nodes[1], child, nodes[2], nodes[3]], first.Children);
        first.Children[3] = child;
        Assert.Equal([nodes[1], nodes[2], child], first.Children);
        Assert.Null(nodes[3].Parent);
        child.Parent = second;
        Assert.Equal([child], second.Children);
        Assert.False(child.IsActiveInHierarchy);
        Assert.Equal(Vector3.UnitY, child.GlobalTransform.Position);
        Assert.DoesNotContain(child, first.FindAllWithTag("Untagged"));
        Assert.Empty(second.FindAllWithTag("Untagged"));
        second.IsActive = true;
        Assert.Equal([second, child], second.FindAllWithTag("Untagged"));
        child.Parent = null;
        Assert.Empty(second.Children);
        Assert.Equal(Vector3.Zero, child.GlobalTransform.Position);
    }

    /// <summary>Assignment copies builders, deduplicates by reference and retains the owned collection instance.</summary>
    [Fact]
    public void ReplaceWithCopiesAndDeduplicatesReferences()
    {
        var root = new Node();
        var owned = root.Children;
        var a = new Node();
        var b = new Node { Id = a.Id };
        NodeCollection builder = [a, b, a];
        Assert.Null(a.Parent);
        root.Children = builder;
        Assert.Same(owned, root.Children);
        Assert.Equal([a, b], root.Children);
        builder.Clear();
        Assert.Equal(2, root.Children.Count);
        var sameId = new Node { Id = a.Id };
        var contains = root.Children.Contains(sameId);
        Assert.False(contains);
        Assert.Equal(-1, root.Children.IndexOf(sameId));
        Assert.False(root.Children.Remove(sameId));
        root.Children.ReplaceWith(root.Children.Reverse());
        Assert.Same(b, root.Children[0]);
        root.Children = root.Children;
        Assert.Equal(2, root.Children.Count);
    }

    /// <summary>Replacing a child list keeps retained registrations and transform stamps through a pure reorder.</summary>
    [Fact]
    public void ReplacementRetainsRegistrationOrderAndTransformVersions()
    {
        var nested = new Node { Tags = ["Enemy"] };
        var first = new Node { Tags = ["Enemy"], Children = [nested] };
        var second = new Node { Tags = ["Enemy"] };
        var root = new Node { Children = [first, second] };
        root.Awake(null);
        var versions = new[] { first.TransformVersion, nested.TransformVersion, second.TransformVersion };

        root.Children = [second, first, second];

        Assert.Equal([second, first], root.Children);
        Assert.Equal([first, nested, second], root.FindAllWithTag("Enemy"));
        Assert.Equal(versions, new[] { first.TransformVersion, nested.TransformVersion, second.TransformVersion });
        root.Children = [first];
        Assert.Equal([first, nested], root.FindAllWithTag("Enemy"));
        Assert.Null(second.Parent);
        Assert.Equal(versions[0], first.TransformVersion);
        Assert.Equal(versions[1], nested.TransformVersion);
    }

    /// <summary>Promoting a descendant before dropping its old parent retains its registration in the same scene.</summary>
    [Fact]
    public void ReplacementPromotesDescendantsWithoutReregistering()
    {
        var promoted = new Node { Tags = ["Enemy"] };
        var removed = new Node { Children = [promoted] };
        var retained = new Node { Tags = ["Enemy"] };
        var root = new Node { Children = [removed, retained] };
        root.Awake(null);
        var version = retained.TransformVersion;

        root.Children = [retained, promoted];

        Assert.Equal([retained, promoted], root.Children);
        Assert.Equal([promoted, retained], root.FindAllWithTag("Enemy"));
        Assert.Same(root, promoted.Parent);
        Assert.Empty(removed.Children);
        Assert.Null(removed.Parent);
        Assert.Equal(version, retained.TransformVersion);
    }

    /// <summary>Unowned builders expose plain ordered-list behavior without changing node membership.</summary>
    [Fact]
    public void BuilderUsesReferencesWithoutReparenting()
    {
        var parent = new Node();
        var nodes = Populate(parent, 4);
        NodeCollection builder = [nodes[0], nodes[1], nodes[0]];
        builder.RemoveAt(1);
        builder.Insert(1, nodes[2]);
        builder[0] = nodes[3];
        builder.Move(2, 0);
        Assert.Equal([nodes[0], nodes[3], nodes[2]], builder);
        Assert.Equal(1, builder.IndexOf(nodes[3]));
        Assert.Equal(-1, builder.IndexOf(nodes[1]));
        var contains = builder.Contains(nodes[3]);
        Assert.True(contains);
        Assert.True(builder.Remove(nodes[3]));
        Assert.False(builder.Remove(nodes[1]));
        Assert.False(builder.Remove(null!));
        Assert.False(builder.IsReadOnly);
        var enumerator = ((System.Collections.IEnumerable)builder).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Same(nodes[0], enumerator.Current);
        Assert.Throws<NotSupportedException>(() => enumerator.Reset());
        builder.ReplaceWith([nodes[3], nodes[3], nodes[0]]);
        Assert.Equal([nodes[3], nodes[0]], builder);
        builder.Clear();
        Assert.All(nodes, node => Assert.Same(parent, node.Parent));
    }

    /// <summary>Serialized hierarchy arrays deserialize into owned collections without format changes.</summary>
    [Fact]
    public void SerializationRoundTripRestoresParentsAndOrder()
    {
        var root = new Node { Children = [new Node { Name = "a" }, new Node { Name = "b" }] };
        root.Children[0].Children.Add(new Node { Name = "nested" });
        var json = Serializer.Serialize(root);
        var copy = Serializer.LoadData<Node>(json)!;
        copy.Awake(null);
        Assert.Equal(["a", "b"], copy.Children.Select(static child => child.Name));
        Assert.All(copy.Children, child => Assert.Same(copy, child.Parent));
        Assert.Same(copy.Children[0], copy.Children[0].Children[0].Parent);
        Assert.DoesNotContain("Version", json);
        Assert.Equal(json, Serializer.Serialize(copy));
    }

    /// <summary>Owned structural changes update polling stamps; reads, no-ops and builder edits do not.</summary>
    [Fact]
    public void VersionsTrackStructuralChanges()
    {
        var root = new Node();
        var nodes = Populate(root, 4);
        root.Children.Remove(nodes[1]);
        var version = root.Children.Version;
        var hierarchy = Node.HierarchyVersion;
        _ = root.Children[1];
        root.Children.Move(0, 0);
        root.Children.Add(nodes[3]);
        nodes[0].Parent = root;
        root.Children[0] = nodes[0];
        Assert.Equal(version, root.Children.Version);
        NodeCollection builder = [new Node()];
        Assert.Equal(version, root.Children.Version);
        root.Children.Insert(0, builder[0]);
        Assert.True(root.Children.Version > version);
        Assert.True(Node.HierarchyVersion > hierarchy);
    }

    /// <summary>Invalid indices, nulls and ancestry cycles fail without changing membership.</summary>
    [Fact]
    public void InvalidMutationsLeaveHierarchyIntact()
    {
        var root = new Node();
        var nodes = Populate(root, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => root.Children.Insert(3, nodes[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.Children.RemoveAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.Children.Move(0, 3));
        Assert.Throws<ArgumentNullException>(() => root.Children.Add(null!));
        Assert.Throws<ArgumentException>(() => nodes[0].Children.Add(root));
        Assert.Throws<ArgumentException>(() => root.Parent = root);
        Assert.Throws<ArgumentException>(() => root.Children.ReplaceWith([nodes[0], root]));
        Assert.Equal(nodes, root.Children);
        Assert.Null(root.Parent);
        var copied = new Node[5];
        root.Children.CopyTo(copied, 1);
        Assert.Equal(nodes, copied[1..4]);
        Assert.Throws<ArgumentException>(() => root.Children.CopyTo(new Node[1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => root.Children.CopyTo(copied, -1));
        Assert.Throws<ArgumentNullException>(() => root.Children.CopyTo(null!, 0));
    }
}
