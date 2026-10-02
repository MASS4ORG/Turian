namespace Turian.Tests;

/// <summary>Tests the scene-wide component queries render systems run every frame.</summary>
public class ComponentQueryTests
{
    [TypeId("a3000004-0000-4000-8000-000000000010")]
    sealed class Tag : Component
    {
        public string Label { get; init; } = "";
    }

    static Node NodeWith(string label, params Node[] children)
    {
        var node = new Node { Name = label };
        node.Components.Add(new Tag { Label = label });
        foreach (var child in children)
        {
            child.Parent = node;
            node.Children.Add(child);
        }

        return node;
    }

    // root → (a → (a1, a2), b → b1), visited depth-first in hierarchy order.
    static Node BuildTree() =>
        NodeWith("root", NodeWith("a", NodeWith("a1"), NodeWith("a2")), NodeWith("b", NodeWith("b1")));

    static string[] Labels(IEnumerable<Tag> tags) => [.. tags.Select(tag => tag.Label)];

    /// <summary>Both query forms visit depth-first in hierarchy order.</summary>
    [Fact]
    public void QueriesVisitDepthFirstInHierarchyOrder()
    {
        var root = BuildTree();
        var results = new List<Tag>();

        root.GetComponentsInChildren(results);

        string[] expected = ["root", "a", "a1", "a2", "b", "b1"];
        Assert.Equal(expected, Labels(results));
        Assert.Equal(expected, Labels(root.GetComponentsInChildren<Tag>()));
        Assert.Equal(expected, Labels(root.GetComponentsInChildren(typeof(Tag)).Cast<Tag>()));
    }

    /// <summary>An inactive node hides its subtree and inactive components are skipped.</summary>
    [Fact]
    public void QueriesSkipInactiveSubtreesAndComponents()
    {
        var root = BuildTree();
        root.Children[0].IsActive = false;
        root.Children[1].Children[0].Components[0].IsActive = false;
        var results = new List<Tag>();

        root.GetComponentsInChildren(results);

        string[] expected = ["root", "b"];
        Assert.Equal(expected, Labels(results));
        Assert.Equal(expected, Labels(root.GetComponentsInChildren<Tag>()));
        Assert.Equal(expected, Labels(root.GetComponentsInChildren(typeof(Tag)).Cast<Tag>()));
    }

    /// <summary>The list form replaces earlier contents, so a reused list never accumulates.</summary>
    [Fact]
    public void ListQueryClearsPreviousResults()
    {
        var root = BuildTree();
        var results = new List<Tag> { new() { Label = "stale" } };

        root.GetComponentsInChildren(results);
        root.GetComponentsInChildren(results);

        Assert.Equal(6, results.Count);
        Assert.DoesNotContain(results, tag => tag.Label == "stale");
    }

    /// <summary>Descendant enumeration excludes the root, keeps hierarchy order and hides inactive subtrees.</summary>
    [Fact]
    public void GetChildrenVisitsActiveDescendantsInOrder()
    {
        var root = BuildTree();
        root.Children[1].IsActive = false;

        Assert.Equal(["a", "a1", "a2"], Node.GetChildren(root).Select(node => node.Name));
        Assert.Empty(Node.GetChildren(null));
    }

    /// <summary>Single-component lookups find the first match by generic and runtime type.</summary>
    [Fact]
    public void GetComponentFindsFirstMatch()
    {
        var node = NodeWith("solo");

        Assert.Equal("solo", node.GetComponent<Tag>()?.Label);
        Assert.Same(node.GetComponent<Tag>(), node.GetComponent(typeof(Tag)));
        Assert.Null(new Node().GetComponent<Tag>());
        Assert.Null(new Node().GetComponent(typeof(Tag)));
    }

    /// <summary>A null root yields nothing rather than throwing.</summary>
    [Fact]
    public void NullRootYieldsNothing()
    {
        Assert.Empty(Node.GetComponentsInChildren<Tag>(null));
        Assert.Empty(Node.GetComponentsInChildren(null, typeof(Tag)));
    }
}
