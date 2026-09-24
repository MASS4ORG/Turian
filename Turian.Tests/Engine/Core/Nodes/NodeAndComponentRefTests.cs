namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="Node.FindById"/> and the <see cref="NodeRef{T}"/>/<see cref="ComponentRef{T}"/>
/// resolution it backs — user-space scripts wire these up in the Inspector to reference specific
/// scene nodes, so resolution has to find the exact node by id, including inactive ones.
/// </summary>
public class NodeAndComponentRefTests
{
    static (Node Root, Node Child, Node Grandchild) BuildTree()
    {
        var root = new Node { Name = "root" };
        var child = new Node { Name = "child" };
        var grandchild = new Node { Name = "grandchild" };
        root.Children.Add(child);
        child.Children.Add(grandchild);
        return (root, child, grandchild);
    }

    /// <summary>The root itself is a valid match, not just its descendants.</summary>
    [Fact]
    public void FindById_MatchesTheRootItself()
    {
        var (root, _, _) = BuildTree();

        Assert.Same(root, Node.FindById(root, root.Id));
    }

    /// <summary>A deeply nested node is found regardless of depth.</summary>
    [Fact]
    public void FindById_FindsADeeplyNestedNode()
    {
        var (root, _, grandchild) = BuildTree();

        Assert.Same(grandchild, Node.FindById(root, grandchild.Id));
    }

    /// <summary>An id nothing in the hierarchy carries returns null rather than throwing.</summary>
    [Fact]
    public void FindById_UnknownId_ReturnsNull()
    {
        var (root, _, _) = BuildTree();

        Assert.Null(Node.FindById(root, Guid.NewGuid()));
    }

    /// <summary>A null root is a miss, not a crash.</summary>
    [Fact]
    public void FindById_NullRoot_ReturnsNull()
    {
        Assert.Null(Node.FindById(null, Guid.NewGuid()));
    }

    /// <summary>
    /// An inactive node still resolves: a reference should not fail just because its target
    /// happens to be disabled at the moment it's looked up.
    /// </summary>
    [Fact]
    public void FindById_FindsAnInactiveNode()
    {
        var (root, child, _) = BuildTree();
        child.IsActive = false;

        Assert.Same(child, Node.FindById(root, child.Id));
    }

    /// <summary>An empty ref resolves to nothing without searching.</summary>
    [Fact]
    public void NodeRef_Empty_ResolvesToNull()
    {
        var (root, _, _) = BuildTree();

        Assert.Null(new NodeRef<Node>().Resolve(root));
    }

    /// <summary>A ref pointing at a real node resolves to it.</summary>
    [Fact]
    public void NodeRef_ResolvesTheReferencedNode()
    {
        var (root, child, _) = BuildTree();

        Assert.Same(child, new NodeRef<Node>(child.Id).Resolve(root));
    }

    /// <summary>A ref whose node no longer exists resolves to null.</summary>
    [Fact]
    public void NodeRef_DanglingReference_ResolvesToNull()
    {
        var (root, _, _) = BuildTree();

        Assert.Null(new NodeRef<Node>(Guid.NewGuid()).Resolve(root));
    }

    /// <summary>A component ref resolves to the component on the referenced node.</summary>
    [Fact]
    public void ComponentRef_ResolvesTheComponentOnTheReferencedNode()
    {
        var (root, child, _) = BuildTree();
        var camera = child.AddComponent<CameraComponent>();

        Assert.Same(camera, new ComponentRef<CameraComponent>(child.Id).Resolve(root));
    }

    /// <summary>A node that exists but has since lost the referenced component resolves to null.</summary>
    [Fact]
    public void ComponentRef_NodeWithoutTheComponent_ResolvesToNull()
    {
        var (root, child, _) = BuildTree();

        Assert.Null(new ComponentRef<CameraComponent>(child.Id).Resolve(root));
    }
}
