using System.Runtime.InteropServices;

namespace Turian.Tests;

/// <summary>Tests the per-frame render list, light upload and UBO layout behind the standard render system.</summary>
public class RenderListTests
{
    const int PointLightsOffset = 160;
    const int DirectionalLightsOffset = PointLightsOffset + (10 * 32);

    static Node NodeWith(string name, params Component[] components)
    {
        var node = new Node { Name = name };
        foreach (var component in components) node.AddComponent(component);
        return node;
    }

    static Node Child(Node parent, Node child)
    {
        child.Parent = parent;
        parent.Children.Add(child);
        return child;
    }

    static Vector4 ReadVector(byte[] bytes, int offset) => MemoryMarshal.Read<Vector4>(bytes.AsSpan(offset));

    /// <summary>One walk collects active models and lights in hierarchy order and skips inactive ones.</summary>
    [Fact]
    public void GatherCollectsActiveModelsAndLightsInOrder()
    {
        var first = new ModelComponent();
        var second = new ModelComponent();
        var hidden = new ModelComponent();
        var disabled = new ModelComponent { IsActive = false };
        var light = LightComponent.CreatePointLight(1f, Vector4.One);
        var a = NodeWith("a", first, light);
        Child(a, NodeWith("a1", second, disabled));
        var off = NodeWith("off", hidden);
        off.IsActive = false;
        var list = new RenderList();

        list.Gather([a, off]);

        Assert.Equal([first, second], list.Models);
        Assert.Equal([light], list.Lights);
    }

    /// <summary>Gathering again replaces the previous frame's content.</summary>
    [Fact]
    public void GatherReplacesPreviousFrame()
    {
        var list = new RenderList();
        list.Gather([NodeWith("a", new ModelComponent(), LightComponent.CreatePointLight(1f, Vector4.One))]);

        list.Gather([NodeWith("b")]);

        Assert.Empty(list.Models);
        Assert.Empty(list.Lights);
    }

    /// <summary>Excluded parents leave included child layers visible, and physics layers do not affect rendering.</summary>
    [Fact]
    public void GatherFiltersRenderLayersIndependentlyOfParentsAndPhysics()
    {
        var parent = NodeWith("parent", new ModelComponent(), new LightComponent());
        parent.RenderLayer = 7;
        var childModel = new ModelComponent();
        var childLight = new LightComponent();
        var child = Child(parent, NodeWith("child", childModel, childLight));
        child.PhysicsLayer = 7;
        var list = new RenderList();
        list.Gather([parent], LayerMask.FromLayer(0));
        Assert.Equal([childModel], list.Models);
        Assert.Equal([childLight], list.Lights);
        list.Gather([parent], LayerMask.Nothing);
        Assert.Empty(list.Models);
        Assert.Empty(list.Lights);
    }

    /// <summary>State numbers are stable per object, first-seen order, and restart after a reset.</summary>
    [Fact]
    public void OrderOfIsStablePerObject()
    {
        var list = new RenderList();
        object first = new(), second = new();

        Assert.Equal(0, list.OrderOf(first));
        Assert.Equal(1, list.OrderOf(second));
        Assert.Equal(0, list.OrderOf(first));

        list.Reset();

        Assert.Equal(0, list.OrderOf(second));
    }

    /// <summary>Draws end up grouped by material first and by model second.</summary>
    [Fact]
    public void SortGroupsByMaterialThenModel()
    {
        var list = new RenderList();
        int[] submeshes = [0, 1, 2, 3];
        (int Material, int Model)[] keys = [(1, 0), (0, 1), (1, 1), (0, 0)];
        foreach (var i in submeshes)
            list.Draws.Add(new DrawItem(null!, i, null!, default, default, RenderList.SortKey(keys[i].Material, keys[i].Model)));

        list.Sort();

        Assert.Equal([3, 1, 0, 2], list.Draws.Select(draw => draw.SubMesh));
    }

    /// <summary>Removing invisible draws preserves scene order among draws sharing the same material and model.</summary>
    [Fact]
    public void SortPreservesSceneOrderWithinEqualKeys()
    {
        var full = new RenderList();
        var culled = new RenderList();
        for (var sequence = 0; sequence < 64; sequence++)
        {
            var draw = new DrawItem(null!, sequence, null!, default, default,
                RenderList.SortKey(sequence % 3, 0), sequence);
            full.Draws.Add(draw);
            if (sequence % 5 != 0) culled.Draws.Add(draw);
        }

        full.Sort();
        culled.Sort();
        var expected = Enumerable.Range(0, 64).OrderBy(sequence => sequence % 3).ThenBy(sequence => sequence);
        Assert.Equal(expected, full.Draws.Select(draw => draw.Sequence));
        Assert.Equal(full.Draws.Where(draw => draw.Sequence % 5 != 0), culled.Draws);
    }

    /// <summary>Lights fill their slots in scene order, past the slot count are dropped, and stale slots clear.</summary>
    [Fact]
    public void UpdateLightsFillsAndClearsSlots()
    {
        var ubo = new GlobalUbo();
        var lights = new List<LightComponent>();
        for (var i = 0; i < ubo.Count + 1; i++)
        {
            var light = LightComponent.CreatePointLight(1f, new Vector4(i, 0, 0, 1));
            NodeWith($"point {i}", light).Position = new Vector3(i, 2, 3);
            lights.Add(light);
        }

        var sun = LightComponent.CreateDirectionalLight(0.5f, Vector4.One);
        NodeWith("sun", sun);
        lights.Add(sun);
        lights.Add(LightComponent.CreatePointLight(1f, Vector4.One));

        StandardRenderSystem.UpdateLights(lights, ubo);
        var bytes = ubo.AsBytes();

        var position = ReadVector(bytes, PointLightsOffset + (4 * 32));
        Assert.Equal(new Vector3(4, 2, 3), new Vector3(position.X, position.Y, position.Z));
        Assert.Equal(uint.MaxValue, BitConverter.SingleToUInt32Bits(position.W));
        Assert.Equal(new Vector4(9, 0, 0, 1), ReadVector(bytes, PointLightsOffset + (9 * 32) + 16));
        Assert.Equal(0.5f, ReadVector(bytes, DirectionalLightsOffset + 16).W);

        StandardRenderSystem.UpdateLights([], ubo);

        Assert.Equal(Vector4.Zero, ReadVector(ubo.AsBytes(), PointLightsOffset + 16));
    }

    /// <summary>The reused UBO buffer holds the same std140 bytes the per-field serializers produce.</summary>
    [Fact]
    public void UboBytesMatchFieldLayout()
    {
        var ubo = new GlobalUbo();
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 100f);
        var view = Matrix4x4.CreateLookAt(new Vector3(1, 2, 3), Vector3.Zero, Vector3.UnitY);
        ubo.Update(projection, view, new Vector4(0, 0, 1, 0));
        ubo.SetPointLightPosition(2, new Vector3(4, 5, 6));
        ubo.SetDirectionalLight(1, Vector3.UnitY, Vector4.One, 2f);

        var expected = new List<byte>();
        expected.AddRange(projection.AsBytes());
        expected.AddRange(view.AsBytes());
        expected.AddRange(new Vector4(0, 0, 1, 0).AsBytes());
        expected.AddRange(new Vector4(1f, 1f, 1f, 0.02f).AsBytes());
        var points = new PointLight[10];
        var directionals = new DirectionalLight[4];
        for (var i = 0; i < points.Length; i++) points[i].SetColor(Vector4.Zero, 0f);
        points[2].SetPosition(new Vector3(4, 5, 6));
        directionals[1].SetDirection(Vector3.UnitY);
        directionals[1].SetColor(Vector4.One, 2f);
        expected.AddRange(points.AsBytes());
        expected.AddRange(directionals.AsBytes());

        var bytes = ubo.AsBytes();

        Assert.Equal(ubo.SizeOf, (uint)bytes.Length);
        Assert.Equal(expected, bytes);
        Assert.Same(bytes, ubo.AsBytes());
    }

    /// <summary>The primary camera is the highest priority, and the first one found on a tie.</summary>
    [Fact]
    public void FindPrimaryPicksHighestPriorityThenFirst()
    {
        var low = new CameraComponent { Priority = 1 };
        var high = new CameraComponent { Priority = 5 };
        var tie = new CameraComponent { Priority = 5 };
        var root = NodeWith("root", low);
        Child(root, NodeWith("high", high));
        Child(root, NodeWith("tie", tie));

        Assert.Same(high, CameraComponent.FindPrimary(root));
        Assert.Null(CameraComponent.FindPrimary(null));
    }
}
