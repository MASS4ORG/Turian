namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="Node.FindById"/> across a hierarchy, including inactive nodes.
/// </summary>
public class NodeSearchTests
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
}
