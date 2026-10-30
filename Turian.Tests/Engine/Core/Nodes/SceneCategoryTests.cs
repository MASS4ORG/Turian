namespace Turian.Tests;

/// <summary>Structural categories support consumers without adding a hierarchy walk to steady frames.</summary>
public class SceneCategoryTests
{
    /// <summary>A consumer can register a category after binding, and unchanged reads never reevaluate its rule.</summary>
    [Fact]
    public void CategoriesRegisterLazilyAndReuseMembership()
    {
        var root = new Node();
        var child = new Node();
        root.Children.Add(child);
        var index = root.EnsureSceneIndex();
        child.Components.Add(new LightComponent());
        var matches = 0;
        var category = new SceneIndex.Category(node =>
        {
            matches++;
            return node.HasComponent<LightComponent>();
        });

        Assert.Equal([child], index.Nodes(category));
        var seeded = matches;
        Assert.Same(index.Nodes(category), index.Nodes(category));
        Assert.Equal(seeded, matches);

        root.Components.Add(new LightComponent());
        Assert.Equal([root, child], index.Nodes(category));
        child.Components.Clear();
        Assert.Equal([root], index.Nodes(category));
    }

    /// <summary>Buffered mesh queries follow sibling and component edits while filtering current activity.</summary>
    [Fact]
    public void BufferedQueriesTrackStructureOrderAndActivity()
    {
        var root = new Node();
        Node a = new(), b = new();
        root.Children = [a, b];
        MeshComponent first = new(), second = new(), last = new();
        a.Components = [first, second];
        b.Components.Add(last);
        var meshes = new List<MeshComponent>();

        root.GetComponentsInChildren(meshes);
        Assert.Equal([first, second, last], meshes);
        a.Components.Move(1, 0);
        root.Children.Move(1, 0);
        root.GetComponentsInChildren(meshes);
        Assert.Equal([last, second, first], meshes);

        last.IsActive = false;
        a.IsActive = false;
        root.GetComponentsInChildren(meshes);
        Assert.Empty(meshes);
        a.IsActive = true;
        a.Components.Remove(second);
        b.Components.Add(first);
        root.GetComponentsInChildren(meshes);
        Assert.Equal([first], meshes);
        first.DestroyImmediate();
        root.GetComponentsInChildren(meshes);
        Assert.Empty(meshes);
    }

    /// <summary>A subtree uses its own activity boundary and excludes other branches in the same scene index.</summary>
    [Fact]
    public void BufferedSubtreeQueriesIgnoreAncestorsAboveTheirRoot()
    {
        var root = new Node { IsActive = false };
        Node branch = new(), inner = new(), outside = new();
        root.Children = [outside, branch];
        branch.Children.Add(inner);
        var mesh = new MeshComponent();
        inner.Components.Add(mesh);
        outside.Components.Add(new MeshComponent());
        var meshes = new List<MeshComponent>();

        branch.GetComponentsInChildren(meshes);
        Assert.Equal([mesh], meshes);
        root.GetComponentsInChildren(meshes);
        Assert.Empty(meshes);
        inner.IsActive = false;
        branch.GetComponentsInChildren(meshes);
        Assert.Empty(meshes);
        branch.DestroyImmediate();
        branch.GetComponentsInChildren(meshes);
        Assert.Empty(meshes);
    }

    /// <summary>Independent categories deduplicate moves across scenes, including removal and reentry before a read.</summary>
    [Fact]
    public void CategoriesTrackTransfersAndReentryByReference()
    {
        Node first = new(), second = new(), traveller = new();
        traveller.Components = [new MeshComponent(), new LightComponent()];
        first.Children.Add(traveller);
        var a = first.EnsureSceneIndex();
        var b = second.EnsureSceneIndex();
        var meshes = SceneIndex.For<MeshComponent>();
        var lights = SceneIndex.For<LightComponent>();
        Assert.Equal([traveller], a.Nodes(meshes));
        Assert.Equal([traveller], a.Nodes(lights));
        Assert.Empty(b.Nodes(meshes));
        Assert.Empty(b.Nodes(lights));

        second.Children.Add(traveller);
        first.Children.Add(traveller);
        second.Children.Add(traveller);
        Assert.Empty(a.Nodes(meshes));
        Assert.Empty(a.Nodes(lights));
        Assert.Equal([traveller], b.Nodes(meshes));
        Assert.Equal([traveller], b.Nodes(lights));

        traveller.Components.Clear();
        traveller.Components.Add(new MeshComponent());
        Assert.Equal([traveller], b.Nodes(meshes));
        Assert.Empty(b.Nodes(lights));
        traveller.Parent = null;
        Assert.Empty(b.Nodes(meshes));
        first.Children.Add(traveller);
        Assert.Equal([traveller], a.Nodes(meshes));
    }
}
