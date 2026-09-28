namespace Turian.Tests;

/// <summary>An inactive node hides its whole subtree from rendering and searches, as Unity's activeInHierarchy.</summary>
public class NodeActiveHierarchyTests
{
    static (Node Root, Node Parent, LightComponent Light) Scene()
    {
        var light = new LightComponent();
        var parent = new Node { Name = "Parent", Children = { new Node { Name = "Child", Components = { light } } } };
        var root = new Node { Name = "Root", Children = { parent } };
        root.Awake(null);
        return (root, parent, light);
    }

    /// <summary>Components under an inactive node are not found.</summary>
    [Fact]
    public void GetComponentsInChildren_SkipsInactiveSubtree()
    {
        var (root, parent, light) = Scene();
        Assert.Contains(light, Node.GetComponentsInChildren<LightComponent>(root));

        parent.IsActive = false;

        Assert.Empty(Node.GetComponentsInChildren<LightComponent>(root));
        Assert.Empty(Node.GetComponentsInChildren(root, typeof(LightComponent)));
    }

    /// <summary>Nodes under an inactive node are not listed.</summary>
    [Fact]
    public void GetChildren_SkipsInactiveSubtree()
    {
        var (root, parent, _) = Scene();

        parent.IsActive = false;

        Assert.Empty(Node.GetChildren(root));
    }
}
