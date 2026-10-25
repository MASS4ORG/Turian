namespace Turian.Tests;

/// <summary>
/// Test Nodes, their hierarchy and Transform operations.
/// </summary>
public class NodeTest
{
    sealed class Node2 : Node;

    readonly Node nodeMain = new() { Name = "Cube -1" };
    readonly int nodeLayers = 10;
    Vector3 positionDefault = new(0, 1, 0);
    Vector3 rotationDefault = new(45, 0, 0);
    Vector3 scaleDefault = new(2, 2, 2);

    /// <summary>
    /// Initializes a new instance of the NodeTest class and sets up the scene hierarchy.
    /// </summary>
    public NodeTest()
    {
        var cubeBase = nodeMain;
        for (var layer = 0; layer < nodeLayers; layer++)
        {
            var cube = layer % 2 == 0 ? new Node() : new Node2();
            cube.Name = $"Cube {layer}";
            cube.Position = positionDefault;
            cube.Rotation = rotationDefault;
            cube.Scale = scaleDefault;
            cubeBase.Children.Add(cube);
            cubeBase = cube;
        }
        nodeMain.Awake(null);
    }

    /// <summary>
    /// Verifies that Node.GetChildren returns the correct number of children.
    /// </summary>
    [Fact]
    public void NodeGetChildren()
    {
        var list = Node.GetChildren(nodeMain).ToList();
        Assert.Equal(10, list.Count);
    }

    /// <summary>
    /// Verifies that Node.GetChildrenByType returns the correct number of typed children.
    /// </summary>
    [Fact]
    public void NodeGetChildrenByType()
    {
        var list = Node.GetChildren<Node2>(nodeMain).ToList();
        Assert.Equal(5, list.Count);
    }

    /// <summary>
    /// Verifies that Node.GetTypeAndChildrenByType returns the correct number of typed nodes.
    /// </summary>
    [Fact]
    public void NodeGetTypeAndChildrenByType()
    {
        var list = Node.GetTypeAndChildren<Node2>(nodeMain.Children).ToList();
        Assert.Equal(5, list.Count);
    }

    /// <summary>
    /// Verifies that global transform rotation is calculated correctly for child nodes.
    /// </summary>
    [Fact]
    public void GlobalTramsformRotation()
    {
        var node = nodeMain;
        var orientation = nodeMain.Transform.Orientation;
        var orientationDefault = rotationDefault.ToQuaternion();
        var layers = 9;
        for (var i = 0; i < layers; i++)
        {
            node = node.Children[0];
            orientation *= orientationDefault;
        }
        var result = rotationDefault * (layers > 0 ? new Vector3(layers, 1, 1) : Vector3.One);
        Assert.Equal($"Cube {layers - 1}", node.Name);
        Assert.Equal(orientation, node.GlobalTransform.Orientation);
        Assert.Equal((double)result.X % 360, node.GlobalTransform.Rotation.X, 3);
    }

    /// <summary>
    /// Verifies that global transform scale is calculated correctly for child nodes.
    /// </summary>
    [Fact]
    public void GlobalTramsformScale()
    {
        var node = nodeMain;
        var expected = Vector3.One;
        var layers = 4;
        for (var i = 0; i < layers; i++)
        {
            node = node.Children[0];
            expected *= scaleDefault;
        }
        Assert.Equal($"Cube {layers - 1}", node.Name);
        Assert.Equal(expected, node.GlobalTransform.Scale);
    }

    /// <summary>
    /// Verifies that global transform position is calculated correctly for child nodes.
    /// </summary>
    [Fact]
    public void GlobalTramsformPosition()
    {
        var node = nodeMain;
        var expected = Mathf.Up;
        var layers = 1;
        for (var i = 0; i < layers; i++)
        {
            node = node.Children[0];
        }
        Assert.Equal($"Cube {layers - 1}", node.Name);
        Assert.Equal(expected, node.GlobalTransform.Position);
    }

    /// <summary>Changing a parent invalidates descendants without changing their local transforms.</summary>
    [Fact]
    public void ChangingParentRotationInvalidatesDescendantGlobalTransform()
    {
        var child = nodeMain.Children[0];
        var before = child.GlobalTransform.Position;

        nodeMain.Rotation = new(0, 0, 90);

        Assert.NotEqual(before, child.GlobalTransform.Position);
        Assert.Equal(positionDefault, child.Transform.Position);
        Assert.Equal(nodeMain.Transform.Orientation * child.Transform.Orientation,
            child.GlobalTransform.Orientation);
    }

    /// <summary>
    /// Verifies that parenting operations correctly update the transform.
    /// </summary>
    [Fact]
    public void GlobalTramsformAddRemoveParent()
    {
        var t1 = new Transform { Position = positionDefault, Rotation = rotationDefault, Scale = scaleDefault };
        var t2 = new Transform { Position = positionDefault * 2, Rotation = rotationDefault * 2, Scale = scaleDefault * 2 };
        var t3 = new Transform { Position = positionDefault * 3, Rotation = rotationDefault * 3, Scale = scaleDefault * 3 };
        var result2 = t2.AddParent(t1).RemoveParent(t1);
        var result3 = t3.AddParent(t2.AddParent(t1)).RemoveParent(t2.AddParent(t1));
        Assert.Equal((double)t2.Position.X, result2.Position.X, 4);
        Assert.Equal((double)t2.Scale.X, result2.Scale.X, 4);
        Assert.Equal((double)t3.Position.X, result3.Position.X, 4);
        Assert.Equal((double)t3.Scale.X, result3.Scale.X, 4);
    }

    /// <summary>
    /// Adding siblings one at a time stays linear, so a scene of many props builds quickly, and removals and
    /// moving an existing child keeps parent links consistent without duplicating it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ManySiblingsAddAndRemoveKeepParentsInLinearTime()
    {
        var root = new Node();
        var watch = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                root.Children.Add(new Node());
            }
        }, TestContext.Current.CancellationToken);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"adding 20,000 children took {watch.Elapsed}");
        Assert.All(root.Children, child => Assert.Same(root, child.Parent));
        var duplicated = root.Children[0];
        root.Children.Add(duplicated);
        Assert.Equal(20_000, root.Children.Count);
        Assert.Same(duplicated, root.Children[^1]);
        root.Children.RemoveAt(0);
        Assert.Same(root, duplicated.Parent);
        root.Children.Remove(duplicated);
        Assert.Null(duplicated.Parent);
        var detached = root.Children[^1];
        detached.Parent = null;
        Assert.DoesNotContain(detached, root.Children);
        Assert.Equal(19_997, root.Children.Count);
    }

    /// <summary>
    /// Clearing and reparenting many siblings compares each removed node a bounded number of times, rather than scanning
    /// the remaining siblings on every removal.
    /// </summary>
    [Fact]
    public void DespawnStormComparesSiblingsLinearly()
    {
        const int count = 2000;
        var parent = new Node();
        var other = new Node();
        var nodes = Enumerable.Range(0, count).Select(static _ => new ComparedNode()).ToArray();
        foreach (var node in nodes)
        {
            parent.Children.Add(node);
        }

        ComparedNode.Comparisons = 0;
        foreach (var node in nodes[..(count / 2)])
        {
            parent.Children.Remove(node);
        }

        foreach (var node in nodes[(count / 2)..])
        {
            other.Children.Add(node);
        }

        Assert.Equal(0, ComparedNode.Comparisons);
        Assert.Empty(parent.Children);
        Assert.All(nodes[..(count / 2)], static node => Assert.Null(node.Parent));
        Assert.All(nodes[(count / 2)..], node => Assert.Same(other, node.Parent));
    }

    /// <summary>Moves, replacements, reparenting and resets keep each child listed once with its parent.</summary>
    [Fact]
    public void ChildEntriesFollowMoveReplaceReparentAndReset()
    {
        var parent = new Node();
        var other = new Node();
        Node a = new(), b = new(), c = new();
        parent.Children = [a, b, c];

        parent.Children.Move(0, 2);
        Assert.All(parent.Children, child => Assert.Same(parent, child.Parent));
        parent.Children.Remove(a);
        Assert.Null(a.Parent);

        parent.Children[0] = b;
        Assert.Same(parent, b.Parent);
        parent.Children[0] = a;
        Assert.Same(parent, a.Parent);
        Assert.Null(b.Parent);

        other.Children.Add(c);
        Assert.Same(other, c.Parent);
        Assert.Equal([a], parent.Children);
        parent.Children.Add(c);
        Assert.Same(parent, c.Parent);
        Assert.Empty(other.Children);

        c.Parent = other;
        Assert.Equal([a], parent.Children);
        other.Children.Add(c);
        parent.Children.Add(b);
        parent.Children.Add(b);
        parent.Children = [b];
        Assert.Null(a.Parent);
        Assert.Same(parent, b.Parent);
        parent.Children.Remove(b);
        Assert.Null(b.Parent);
        parent.Children.Clear();
        Assert.Same(other, c.Parent);
        Assert.Equal([c], other.Children);
    }

    /// <summary>A node that counts equality comparisons, which every child-list scan performs.</summary>
    sealed class ComparedNode : Node
    {
        public static int Comparisons { get; set; }

        public override bool Equals(object? obj)
        {
            Comparisons++;
            return base.Equals(obj);
        }

        public override int GetHashCode() => base.GetHashCode();
    }

    [TypeId("a3000004-0000-4000-8000-000000000005")]
    class TestComponent : Component { }

    /// <summary>
    /// Verifies that components can be added to and retrieved from a node.
    /// </summary>
    [Fact]
    public void Node_AddComponent_GetComponent()
    {
        var node = new Node();
        var comp = new TestComponent();
        node.Components.Add(comp);
        comp.Setup(node);
        Assert.Equal(comp, node.GetComponent<TestComponent>());
    }

    /// <summary>
    /// Verifies that components can be retrieved from a node and its children.
    /// </summary>
    [Fact]
    public void Node_GetComponentsInChildren()
    {
        var root = new Node();
        var child = new Node();
        root.Children.Add(child);
        var compRoot = new TestComponent();
        var compChild = new TestComponent();
        root.Components.Add(compRoot);
        compRoot.Setup(root);
        child.Components.Add(compChild);
        compChild.Setup(child);
        var comps = root.GetComponentsInChildren<TestComponent>().ToList();
        Assert.Equal(2, comps.Count);
    }

    [TypeId("a3000004-0000-4000-8000-000000000007")]
    class MockNodeComponent : Component
    {
        public bool Enabled { get; private set; }
        public override void OnEnable() => Enabled = true;
        public override void OnDisable() => Enabled = false;
    }

    /// <summary>
    /// Verifies that component enablement state propagates when a node is activated/deactivated.
    /// </summary>
    [Fact]
    public void Node_Activation_Deactivation_Propagation()
    {
        var root = new Node();
        var comp = new MockNodeComponent();
        root.Components.Add(comp);
        comp.Setup(root);
        Assert.True(comp.Enabled);
        root.IsActive = false;
        Assert.False(comp.Enabled);
        root.IsActive = true;
        Assert.True(comp.Enabled);
    }
}
