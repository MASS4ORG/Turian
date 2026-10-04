namespace Turian.Tests;

/// <summary>Checks editor copies retain layers and own their tag collections.</summary>
public sealed class NodeLayerCloneTests
{
    /// <summary>Editing a copied node's tags does not change the source or its registry.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesPreserveLayersAndHaveIndependentTags(bool deep)
    {
        var source = new Node { Name = "Original", Tags = ["Enemy"], PhysicsLayer = 7, RenderLayer = 31 };
        var root = new Node { Children = [source] };
        Assert.Same(source, root.FindWithTag("Enemy"));
        var clone = deep ? NodeCloner.DeepClone(source, awake: false)!
            : NodeCloner.ShallowClone(source, "Copy");
        Assert.Equal(7, clone.PhysicsLayer);
        Assert.Equal(31, clone.RenderLayer);
        Assert.Equal(["Enemy"], clone.Tags);
        Assert.NotSame(source.Tags, clone.Tags);
        clone.Tags.Clear();
        clone.Tags.Add("Player");
        Assert.Equal(["Enemy"], source.Tags);
        Assert.Same(source, root.FindWithTag("Enemy"));
        Assert.Null(root.FindWithTag("Player"));
    }
}
