namespace Turian.Tests;

/// <summary>Checks the JSON clipboard copies hierarchies and component values as the scene tree and inspector do.</summary>
[Collection(SerialTests.Name)]
public sealed class NodeClipboardTests
{
    readonly AssetManager assets = new();
    readonly SceneTreeController sceneTree;
    readonly NodeInspectorController inspector;
    readonly UndoService undo;
    readonly Prefab scene = new() { Id = Guid.NewGuid(), RelativePath = "Assets/scene.prefab" };
    readonly Node root = new() { Name = "Root" };

    /// <summary>Builds a scene tree over an in-memory root.</summary>
    public NodeClipboardTests()
    {
        var loader = Substitute.For<IAssetLoader>();
        sceneTree = new SceneTreeController(assets, new SettingsService(), assetImporter: null!,
            assetLoader: loader, sceneManager: Substitute.For<ISceneManager>(), database: new AssetDatabase());
        inspector = new NodeInspectorController(assets);
        undo = new UndoService(sceneTree, inspector, assets, loader);
        assets.OpenAsset(scene);
        sceneTree.OpenAsset(scene);
        typeof(SceneTreeController).GetField("sceneRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sceneTree, root);
    }

    Node AddChild(string name)
    {
        var child = new Node { Name = name, Parent = root };
        root.Children.Add(child);
        return child;
    }

    /// <summary>A copied subtree's internal references point at the copies, not the originals.</summary>
    [Fact]
    public void CopyingRemapsReferencesInsideTheCopiedGroup()
    {
        var first = AddChild("First");
        var second = AddChild("Second");
        first.AddComponent(new LinkComponent { Target = second, Value = 5f });

        var json = sceneTree.SerializeNodes([first, second]);
        Assert.True(NodeClipboard.HoldsNodes(json));
        var copies = sceneTree.PasteNodes(json);

        Assert.Equal(2, copies.Count);
        Assert.NotEqual(first.Id, copies[0].Id);
        Assert.NotEqual(second.Id, copies[1].Id);
        var link = copies[0].GetComponent<LinkComponent>()!;
        Assert.Equal(5f, link.Value);
        Assert.Same(copies[1], link.Target);
        Assert.NotSame(second, link.Target);
    }

    /// <summary>Pasting as a child or a sibling lands the copy where the caller asked, as one undo step.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PasteAttachesWhereAskedAndUndoesInOneStep(bool asChild)
    {
        var source = AddChild("Source");
        var json = sceneTree.SerializeNodes([source]);
        var operations = new NodeSelectionOperations(sceneTree, inspector, undo);
        var clones = sceneTree.PasteNodes(json);

        operations.Paste(clones, source, asChild);

        if (asChild)
        {
            Assert.Single(source.Children);
            Assert.Same(source, source.Children[0].Parent);
            Assert.NotSame(source, source.Children[0]);
        }
        else
        {
            Assert.Same(root, clones[0].Parent);
            Assert.Equal(2, root.Children.Count);
        }

        Assert.Single(undo.History.UndoSteps);
        undo.Undo();
        Assert.Equal(["Source"], root.Children.Select(static child => child.Name));
        Assert.Empty(source.Children);
    }

    /// <summary>Component values paste onto a same-typed component on another node, leaving its identity alone.</summary>
    [Fact]
    public void ComponentValuesPasteOntoAnotherNode()
    {
        var owner = AddChild("Owner");
        var target = AddChild("Target");
        owner.AddComponent(new LinkComponent { Value = 7f });
        target.AddComponent(new LinkComponent { Value = 1f });

        var json = NodeClipboard.WriteComponent(owner.GetComponent<LinkComponent>()!);
        Assert.True(NodeClipboard.TryReadComponent(json, loader: null, out var copied));

        var destination = target.GetComponent<LinkComponent>()!;
        Assert.True(inspector.ApplyComponentValues(destination, copied!));

        Assert.Equal(7f, destination.Value);
        Assert.Same(target, destination.Node);
        Assert.Same(destination, target.GetComponent<LinkComponent>());
    }

    /// <summary>Paste as new adds a second component of the copied type carrying its values.</summary>
    [Fact]
    public void PasteComponentAsNewAddsACopy()
    {
        var owner = AddChild("Owner");
        owner.AddComponent(new LinkComponent { Value = 3f });
        var json = NodeClipboard.WriteComponent(owner.GetComponent<LinkComponent>()!);
        Assert.True(NodeClipboard.TryReadComponent(json, loader: null, out var copied));

        inspector.Select(owner);
        var added = inspector.PasteComponentAsNew(copied!);

        Assert.NotNull(added);
        Assert.Equal(3f, ((LinkComponent)added!).Value);
        Assert.Equal(2, owner.Components.Count);
    }

    /// <summary>Plain text and envelopes of the other kind are refused.</summary>
    [Fact]
    public void OnlyMatchingEnvelopesAreAccepted()
    {
        Assert.False(NodeClipboard.HoldsNodes("hello"));
        Assert.False(NodeClipboard.HoldsNodes(null));
        Assert.False(NodeClipboard.TryReadNodes("hello", null, out var nodes));
        Assert.Empty(nodes);

        var component = NodeClipboard.WriteComponent(new LinkComponent { Value = 2f });
        Assert.True(NodeClipboard.HoldsComponent(component));
        Assert.False(NodeClipboard.HoldsNodes(component));
    }

    /// <summary>Envelopes with the wrong shape, or an unknown component type, read as empty.</summary>
    [Fact]
    public void MalformedEnvelopesReadAsEmpty()
    {
        Assert.False(NodeClipboard.TryReadNodes("""{"kind":"turian.nodes","version":1,"nodes":{}}""", null,
            out var noArray));
        Assert.Empty(noArray);

        Assert.False(NodeClipboard.TryReadNodes("""{"kind":"turian.nodes","version":1,"nodes":[null]}""", null,
            out var onlyNulls));
        Assert.Empty(onlyNulls);

        Assert.False(NodeClipboard.TryReadComponent("""{"kind":"turian.component","version":1}""", null,
            out var missing));
        Assert.Null(missing);

        Assert.False(NodeClipboard.TryReadComponent(
            """{"kind":"turian.component","version":1,"component":{"__TypeId":"does.not.exist"}}""", null, out _));

        Assert.Empty(sceneTree.PasteNodes("hello"));
    }

    /// <summary>Paste as new needs a selected node to receive the component.</summary>
    [Fact]
    public void PasteComponentAsNewNeedsASelectedNode()
    {
        inspector.ClearSelection();

        Assert.Null(inspector.PasteComponentAsNew(new LinkComponent { Value = 1f }));
    }

    [TypeId("c1b0f2a0-0000-4000-8000-000000000001")]
    sealed class LinkComponent : Component
    {
        public Node? Target { get; set; }
        public float Value { get; set; }
    }
}
