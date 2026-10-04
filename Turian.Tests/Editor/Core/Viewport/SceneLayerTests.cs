namespace Turian.Tests;

/// <summary>Checks the independent effects of Scene view layer visibility and interaction locks.</summary>
public sealed class SceneLayerTests
{
    /// <summary>Hidden layers can be picked; locked layers cannot, including when they are the nearest hit.</summary>
    [Fact]
    public void LocksSkipPickingWhileVisibilityOnlyHidesRendering()
    {
        var near = new Node { RenderLayer = 4 };
        var far = new Node { RenderLayer = 0 };
        var view = new SceneViewSettings();
        var ray = new Ray(Vector3.Zero, Vector3.UnitZ);
        (Node Node, Bounds WorldBounds)[] candidates =
        [
            (near, new Bounds(new(-1, -1, 2), new(1, 1, 3))),
            (far, new Bounds(new(-1, -1, 4), new(1, 1, 5))),
        ];
        view.SetVisible(4, false);
        Assert.False(view.VisibleLayers.Contains(4));
        Assert.Same(near, ScenePicker.PickClosest(candidates, ray, view));
        view.SetLocked(4, true);
        Assert.Same(far, ScenePicker.PickClosest(candidates, ray, view));
        view.SetLocked(0, true);
        Assert.Null(ScenePicker.PickClosest(candidates, ray, view));
        view.SetLocked(4, false);
        view.SetVisible(4, true);
        Assert.True(view.CanSelect(near));
        Assert.True(view.VisibleLayers.Contains(4));
    }

    /// <summary>Selection rejects locked nodes and attached components and clears selections on a new lock.</summary>
    [Fact]
    public void LocksApplyToAllSelectionPaths()
    {
        var view = new SceneViewSettings();
        var selection = new NodeInspectorController(new AssetManager(), viewSettings: view);
        var node = new Node { RenderLayer = 5 };
        var light = new LightComponent();
        node.AddComponent(light);
        selection.Select(node);
        Assert.Same(node, selection.SelectedNode);
        view.SetLocked(5, true);
        Assert.Null(selection.SelectedObject);
        selection.Select(node);
        Assert.Null(selection.SelectedNode);
        selection.Select(light);
        Assert.Null(selection.SelectedNode);
        view.SetLocked(5, false);
        selection.Select(light);
        Assert.Same(node, selection.SelectedNode);
    }

    /// <summary>Layer locks stop an active gizmo drag and block subsequent transform gestures.</summary>
    [Fact]
    public void LocksBlockAndEndGizmoInteraction()
    {
        var node = new Node { Position = new(0, 0, 5), RenderLayer = 3 };
        var view = new SceneViewSettings();
        var gizmo = new TransformGizmo { SelectedNode = node, ViewSettings = view };
        var camera = new EditorCamera();
        var size = new Vector2(800, 600);
        camera.Resize(800, 600);
        var center = (camera.Project(node.Position) + Vector2.One) * 0.5f * size;
        gizmo.ProcessPointerDown(center, camera, size);
        Assert.True(gizmo.IsDragging);
        var before = node.Transform;
        view.SetLocked(3, true);
        gizmo.ProcessPointerMove(center + new Vector2(30, 0), camera, size);
        Assert.False(gizmo.IsDragging);
        Assert.Equal(before, node.Transform);
        gizmo.ProcessPointerDown(center, camera, size);
        Assert.False(gizmo.IsDragging);
    }

    /// <summary>Inspector validation reports unnamed node layers without modifying node data.</summary>
    [Fact]
    public void NodeValidationReportsUnnamedSlots()
    {
        var settings = new TagsAndLayersSettings();
        settings.PhysicsLayers[3].Name = "";
        var node = new Node { PhysicsLayer = 3, RenderLayer = 99 };
        Assert.Equal(2, TagsAndLayersValidation.Warnings(node, settings).Count);
        Assert.Equal(3, node.PhysicsLayer);
        Assert.Equal(99, node.RenderLayer);
        Assert.Empty(TagsAndLayersValidation.Warnings(new object(), settings));
        Assert.Equal(settings.Validate(), TagsAndLayersValidation.Warnings(settings, settings));
    }
}
