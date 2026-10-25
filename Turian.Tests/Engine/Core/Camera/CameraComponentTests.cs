namespace Turian.Tests;

/// <summary>A camera's background color: its default and how scenes store it.</summary>
public class CameraComponentTests
{
    /// <summary>Primary selection preserves hierarchy ties, priorities and inactive-subtree filtering.</summary>
    [Fact]
    public void FindPrimaryFollowsHierarchyOrderAndActivity()
    {
        var root = new Node();
        var first = new Node();
        var later = new Node();
        root.Children = [first, later];
        var a = first.AddComponent(new CameraComponent { Priority = 1 });
        var b = later.AddComponent(new CameraComponent { Priority = 1 });
        Assert.Same(a, CameraComponent.FindPrimary(root));
        root.Children.Move(1, 0);
        Assert.Same(b, CameraComponent.FindPrimary(root));
        var nested = new Node();
        first.Children.Add(nested);
        var high = nested.AddComponent(new CameraComponent { Priority = 10 });
        first.IsActive = false;
        Assert.Same(b, CameraComponent.FindPrimary(root));
        first.IsActive = true;
        Assert.Same(high, CameraComponent.FindPrimary(root));
        high.IsActive = false;
        Assert.Same(b, CameraComponent.FindPrimary(root));
        root.IsActive = false;
        Assert.Null(CameraComponent.FindPrimary(root));
        Assert.Null(CameraComponent.FindPrimary(null));
    }

    /// <summary>Repeated primary selection reuses its traversal storage after warming a large scene.</summary>
    [Fact]
    public void FindPrimaryDoesNotAllocatePerSceneTraversal()
    {
        var root = new Node();
        var camera = root.AddComponent(new CameraComponent());
        for (var i = 0; i < 2048; i++) { root.Children.Add(new Node()); }
        for (var i = 0; i < 10; i++) { _ = CameraComponent.FindPrimary(root); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        CameraComponent? actual = null;
        for (var i = 0; i < 100; i++) { actual = CameraComponent.FindPrimary(root); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(camera, actual);
        Assert.True(allocated < 4096, $"Primary-camera traversal allocated {allocated} bytes after warm-up.");
    }

    /// <summary>A new camera clears with the light blue the Scene view uses by default.</summary>
    [Fact]
    public void BackgroundColorDefaultsToTheSceneViewSky()
    {
        Assert.Equal(new Vector4(new SceneViewSettings().EmptySkyColor, 1f), (Vector4)new CameraComponent().BackgroundColor);
    }

    /// <summary>A scene keeps an authored background, and a scene saved before the property loads the default.</summary>
    [Fact]
    public void BackgroundColorRoundTripsAndDefaultsInOlderScenes()
    {
        var node = new Node();
        node.AddComponent(new CameraComponent { BackgroundColor = new Color(1f, 0.5f, 0.25f, 0.75f) });

        var json = Serializer.Serialize(node);
        var loaded = Serializer.LoadData<Node>(json)!.GetComponent<CameraComponent>()!;
        Assert.Equal(new Color(1f, 0.5f, 0.25f, 0.75f), loaded.BackgroundColor);

        var older = Regex.Replace(json, "\"BackgroundColor\":\\s*\\{[^}]*\\},?", "");
        Assert.DoesNotContain("BackgroundColor", older);
        Assert.Equal(new CameraComponent().BackgroundColor,
            Serializer.LoadData<Node>(older)!.GetComponent<CameraComponent>()!.BackgroundColor);
    }
}
