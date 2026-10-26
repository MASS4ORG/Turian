namespace Turian.Tests;

/// <summary>Qualifies curves, world/local ribbons, trail timing and text placement without a GPU.</summary>
public sealed class WorldGraphicsComponentTests
{
    static EditorCamera Camera() => new();

    /// <summary>Curves clamp and interpolate, copy caller keys and reject malformed samples.</summary>
    [Fact]
    public void CurvesInterpolateAndValidate()
    {
        CurveKey[] keys = [new(0.2f, 2), new(0.8f, 4)];
        var curve = new FloatCurve { Keys = keys };
        keys[0] = new(0.2f, 99);
        Assert.Equal(2, curve.Evaluate(-1));
        Assert.Equal(3, curve.Evaluate(0.5f));
        Assert.Equal(4, curve.Evaluate(2));
        Assert.Equal(7, new FloatCurve { Keys = [new(0.5f, 7)] }.Evaluate(1));
        var gradient = new ColorGradient { Keys = [new(0, new Color(1, 0, 0, 0)), new(1, new Color(0, 0, 1, 1))] };
        Assert.Equal(new Color(0.5f, 0, 0.5f, 0.5f), gradient.Evaluate(0.5f));
        Assert.Equal(new Color(1, 0, 0, 0), gradient.Evaluate(-1));
        Assert.Equal(new Color(0, 0, 1, 1), gradient.Evaluate(2));
        Assert.Equal(Color.White, new ColorGradient().Evaluate(0));
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [] });
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [new(0, 1), new(0, 2)] });
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [new(-1, 1)] });
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [new(2, 1)] });
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [new(float.NaN, 1)] });
        Assert.Throws<ArgumentException>(() => new FloatCurve { Keys = [new(0, float.NaN)] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new FloatCurve { Keys = [new(0, -1)] });
        Assert.Throws<ArgumentException>(() => curve.Evaluate(float.NaN));
        Assert.Throws<ArgumentException>(() => new ColorGradient { Keys = [new(0, new Color(float.NaN, 0, 0))] });
    }

    /// <summary>Interior keys add vertices so a two-point line can taper and change color midway.</summary>
    [Fact]
    public void RibbonsSampleInteriorKeysAndPreserveAlpha()
    {
        var line = new LineRenderer
        {
            Positions = [Vector3.Zero, new Vector3(4, 0, 0)],
            Billboard = false,
            WidthCurve = new() { Keys = [new(0, 0), new(0.5f, 2), new(1, 0)] },
            ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0, 0)), new(1, new Color(0, 0, 1, 1))] },
        };
        var geometry = line.BuildGeometry(Camera());
        Assert.Equal(6, geometry.Vertices.Length);
        Assert.Equal(new Vector3(2, -1, 0), geometry.Vertices[2].Position);
        Assert.Equal(new Vector4(0.5f, 0, 0.5f, 0.5f), geometry.Vertices[2].Color);
        Assert.Equal(new Vector2(1, 1), geometry.Vertices[^1].Uv);
        line.TextureMode = LineTextureMode.Tile;
        Assert.Equal(new Vector2(4, 1), line.BuildGeometry(Camera()).Vertices[^1].Uv);
        line.Positions = [Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX * 2];
        Assert.All(line.BuildGeometry(Camera()).Vertices, static vertex => Assert.True(float.IsFinite(vertex.Position.X)));
        line.Positions = [Vector3.Zero, Vector3.Zero];
        Assert.Empty(line.BuildGeometry(Camera()).Vertices);
        line.Positions = [];
        Assert.Empty(line.BuildGeometry(Camera()).Vertices);
        line.Positions = [Vector3.One];
        Assert.Empty(line.BuildGeometry(Camera()).Indices);
        line.Positions = [Vector3.Zero, new(0.0000006f, 0, 0), new(0.0000012f, 0, 0), Vector3.UnitX];
        Assert.All(line.BuildGeometry(Camera()).Vertices, static vertex => Assert.True(float.IsFinite(vertex.Color.W)));
    }

    /// <summary>Local placement follows parent translation, rotation and nonuniform scale; world points stay fixed.</summary>
    [Fact]
    public void WorldAndLocalSpacesHaveDistinctPlacement()
    {
        var parent = new Node { Position = new Vector3(10, 2, 0) };
        var node = new Node { Parent = parent, Transform = new Transform { Scale = new(2, 3, 1), Rotation = new(0, 0, 90) } };
        var line = new LineRenderer { Positions = [Vector3.Zero, Vector3.UnitX], Billboard = false };
        node.AddComponent(line);
        parent.Awake(null);
        var world = line.BuildGeometry(Camera());
        Assert.Equal(new Vector3(0, -0.5f, 0), world.Vertices[0].Position);
        line.UseWorldSpace = false;
        var local = line.BuildGeometry(Camera());
        var expected = Vector3.Transform(world.Vertices[0].Position, node.GlobalTransform.Matrix4X4());
        Assert.True(Vector3.Distance(expected, local.Vertices[0].Position) < 0.001f);
        var camera = Camera();
        camera.Yaw = MathF.PI / 2;
        line.Billboard = true;
        var facing = line.BuildGeometry(camera);
        Assert.NotEqual(local.Vertices[0].Position, facing.Vertices[0].Position);
        Assert.InRange(MathF.Abs(Vector3.Dot(facing.Vertices[1].Position - facing.Vertices[0].Position,
            camera.Front)), 0, 0.001f);
        node.Transform = new Transform { Scale = Vector3.Zero };
        Assert.Empty(line.BuildGeometry(camera).Vertices);
    }

    /// <summary>Trail points stay in world space, age with simulation time and respect emission and retention controls.</summary>
    [Fact]
    public void TrailsExpireAndClearWithBoundedHistory()
    {
        var node = new Node();
        var trail = new TrailRenderer
        {
            Time = 1,
            MinVertexDistance = 0.2f,
            MaxPoints = 3,
            ColorGradient = new() { Keys = [new(0, Color.White), new(1, new Color(1, 1, 1, 0))] },
        };
        node.AddComponent(trail);
        node.Awake(null);
        trail.OnLateUpdate(0);
        node.Position = new Vector3(0.1f, 0, 0);
        trail.OnLateUpdate(0.1f);
        Assert.Equal(1, trail.PositionCount);
        for (var i = 1; i <= 4; i++)
        {
            node.Position = new Vector3(i, 0, 0);
            trail.OnLateUpdate(0.1f);
        }
        Assert.Equal(3, trail.PositionCount);
        var geometry = trail.BuildGeometry(Camera());
        Assert.Equal(6, geometry.Vertices.Length);
        Assert.InRange(geometry.Vertices[0].Color.W, 0.79f, 0.81f);
        Assert.Equal(2, geometry.Vertices[0].Position.X);
        trail.UseWorldSpace = false;
        Assert.Equal(geometry.Vertices, trail.BuildGeometry(Camera()).Vertices);
        trail.Emitting = false;
        trail.OnLateUpdate(1);
        Assert.Empty(trail.BuildGeometry(Camera()).Vertices);
        trail.Emitting = true;
        trail.OnLateUpdate(0);
        trail.OnLateUpdate(0);
        Assert.Equal(1, trail.PositionCount);
        trail.Clear();
        Assert.Equal(0, trail.PositionCount);
        trail.OnLateUpdate(0);
        trail.IsActive = false;
        Assert.Equal(0, trail.PositionCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.Time = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.MinVertexDistance = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.MaxPoints = 1);
        Assert.Throws<ArgumentException>(() => trail.OnLateUpdate(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => trail.OnLateUpdate(-1));
    }

    /// <summary>Lifetime keys between motion samples are retained without extrapolating beyond the recorded path.</summary>
    [Fact]
    public void TrailCurvesRetainInteriorLifetimeKeys()
    {
        var node = new Node();
        var trail = new TrailRenderer
        {
            Time = 1, MinVertexDistance = 0,
            WidthCurve = new() { Keys = [new(0, 0), new(0.5f, 1), new(1, 0)] },
        };
        node.AddComponent(trail);
        node.Awake(null);
        trail.OnLateUpdate(0);
        node.Position = new(4, 0, 0);
        trail.OnLateUpdate(0.8f);
        var geometry = trail.BuildGeometry(Camera());
        Assert.Equal(6, geometry.Vertices.Length);
        Assert.InRange(geometry.Vertices[2].Position.X, 1.49f, 1.51f);
        Assert.Equal(1, Vector3.Distance(geometry.Vertices[2].Position, geometry.Vertices[3].Position));
        Assert.All(geometry.Vertices, static vertex => Assert.InRange(vertex.Position.X, 0, 4));
        trail.OnLateUpdate(0);
        Assert.Equal(2, trail.PositionCount);
        trail.UseWorldSpace = false;
        node.Scale = Vector3.Zero;
        Assert.Empty(trail.BuildGeometry(Camera()).Vertices);
        new TrailRenderer().OnLateUpdate(0);
    }

    /// <summary>Text anchors and billboard axes place the same rectangle independently for each camera.</summary>
    [Fact]
    public void TextSupportsAnchorsAndPerCameraBillboards()
    {
        var node = new Node { Position = new(2, 3, 4), Transform = new Transform { Position = new(2, 3, 4), Rotation = new(0, 90, 0) } };
        var text = new WorldTextComponent { Anchor = Vector2.Zero, Billboard = false, Color = new Color(1, 0, 0, 0.25f) };
        node.AddComponent(text);
        node.Awake(null);
        var camera = Camera();
        var fixedGeometry = text.BuildGeometry(camera, new(2, 1));
        Assert.Equal(node.Position, fixedGeometry.Vertices[0].Position);
        Assert.True(MathF.Abs(fixedGeometry.Vertices[1].Position.Z - node.Position.Z) > 1.9f);
        text.Billboard = true;
        var facing = text.BuildGeometry(camera, new(2, 1));
        Assert.True(Vector3.Distance(node.Position + camera.Right * 2, facing.Vertices[1].Position) < 0.001f);
        Assert.Equal(new Vector4(1, 0, 0, 0.25f), facing.Vertices[0].Color);
        Assert.Equal(new Vector2(0, 1), facing.Vertices[0].Uv);
        text.Anchor = new(0.5f);
        var centered = text.BuildGeometry(camera, new(2, 1));
        Assert.True(Vector3.Distance(node.Position, (centered.Vertices[0].Position + centered.Vertices[3].Position) * 0.5f) < 0.001f);
        Assert.Throws<ArgumentOutOfRangeException>(() => text.FontSize = 0);
        Assert.Throws<ArgumentException>(() => text.FontSize = float.NaN);
    }

    /// <summary>Scene serialization preserves the public feature settings and excludes trail runtime samples.</summary>
    [Fact]
    public void SceneRoundTripsAllGraphicComponents()
    {
        var node = new Node();
        node.AddComponent(new LineRenderer
        {
            Positions = [Vector3.One, Vector3.UnitX],
            UseWorldSpace = false,
            Billboard = false,
            WidthCurve = new() { Keys = [new(0, 0.2f), new(1, 0.4f)] },
            ColorGradient = new() { Keys = [new(0, new Color(1, 0, 0, 0.3f))] },
            TextureMode = LineTextureMode.Tile,
            Texture = new(Guid.NewGuid()),
        });
        node.AddComponent(new TrailRenderer { Time = 2, MinVertexDistance = 0.3f, Emitting = false });
        node.AddComponent(new WorldTextComponent { Text = "Damage\n+12", Font = "sans-serif", Billboard = false, FontSize = 0.7f });
        var loaded = Serializer.LoadData<Node>(Serializer.Serialize(node))!;
        var line = loaded.Components.OfType<LineRenderer>().First();
        Assert.Equal(new[] { Vector3.One, Vector3.UnitX }, line.Positions);
        Assert.False(line.UseWorldSpace);
        Assert.False(line.Billboard);
        Assert.Equal(0.3f, line.ColorGradient.Evaluate(0).A);
        Assert.Equal(0.4f, line.WidthCurve.Evaluate(1));
        Assert.Equal(LineTextureMode.Tile, line.TextureMode);
        Assert.NotNull(line.Texture);
        Assert.Equal(2, loaded.GetComponent<TrailRenderer>()!.Time);
        Assert.Equal(0, loaded.GetComponent<TrailRenderer>()!.PositionCount);
        Assert.Equal("Damage\n+12", loaded.GetComponent<WorldTextComponent>()!.Text);
        Assert.Equal(0.7f, loaded.GetComponent<WorldTextComponent>()!.FontSize);
    }

    /// <summary>SDF values increase smoothly through a contour and saturate for empty and filled masks.</summary>
    [Fact]
    public void SignedDistanceFieldsEncodeInsideAndOutside()
    {
        byte[] mask = [0, 0, 0, 255, 255, 255, 255];
        var sdf = SignedDistanceField.Build(mask, 7, 1, 2);
        Assert.Equal(0, sdf[0]);
        Assert.True(sdf[2] < 128);
        Assert.True(sdf[3] > 128);
        Assert.Equal(255, sdf[^1]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, SignedDistanceField.Build(new byte[4], 2, 2));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, SignedDistanceField.Build([255, 255, 255, 255], 2, 2));
        Assert.Throws<ArgumentException>(static () => SignedDistanceField.Build([0], 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(static () => SignedDistanceField.Build([], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(static () => SignedDistanceField.Build([0], 1, 1, 0));
    }
}
