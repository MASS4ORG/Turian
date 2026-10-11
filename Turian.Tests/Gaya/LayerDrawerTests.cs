namespace Turian.Tests;

/// <summary>Checks layer picker clipping, scrolling and edits through both headless GUI passes.</summary>
public sealed class LayerDrawerTests
{
    sealed class Harness : IDisposable
    {
        readonly SKSurface surface = SKSurface.Create(new SKImageInfo(320, 260));
        readonly Font font = Font.FromFamilyName("sans-serif", 14);
        readonly LayerDrawer drawer;
        readonly FormField field;
        readonly FormRenderContext context = new();
        internal readonly IInputHandler Input = Substitute.For<IInputHandler>();
        internal readonly Gui Gui;

        internal Harness(object target, string member, TagsAndLayersSettings? settings = null)
        {
            var project = new AppSettings();
            if (settings is not null) project.Loaded.Use(settings);
            drawer = new LayerDrawer(new LayerFilter(project));
            field = FormField.ForMember(target.GetType().GetProperty(member)!, target);
            Gui = new Gui { Input = Input };
            Input.MousePosition.Returns(new Vector2(-1));
            Input.GetTypedCharacters().Returns(string.Empty);
            Frame();
        }

        internal void Frame()
        {
            var before = field.GetValue();
            object? builtValue = null;
            surface.Canvas.Clear(SKColors.Transparent);
            InspectorFormsRenderingTests.Frame(Gui, surface, font, gui =>
            {
                using (gui.Node(300, 250, "layer-panel").Direction(Axis.Vertical).Enter())
                    drawer.Draw(gui, field, "layer-field", context);
                if (gui.Pass == Pass.Pass1Build)
                {
                    if (field.ValueType != typeof(LayerMask)) Assert.Equal(before, field.GetValue());
                    builtValue = field.GetValue();
                }
                else if (field.ValueType == typeof(LayerMask)) Assert.Equal(builtValue, field.GetValue());
            });
        }

        internal LayoutNode Button => Nodes().Single(node => node.Id.EndsWith("/button", StringComparison.Ordinal));
        internal LayoutNode List => Nodes().Single(node => node.Id.EndsWith("/list", StringComparison.Ordinal));
        internal LayoutNode Option(int index) => Nodes().Single(node =>
            node.Id.EndsWith(field.ValueType == typeof(LayerMask) ? $"/option/{index}" : $"/list/{index}",
                StringComparison.Ordinal));

        internal void ClickAction(string action) =>
            Click(Nodes().Single(node => node.Id.EndsWith($"/{action}", StringComparison.Ordinal)).Rect.Center);

        internal void Click(Vector2 position)
        {
            Input.MousePosition.Returns(position);
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
            Frame();
            Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
            Frame();
        }

        internal void Open() => Click(Button.Rect.Center);

        internal void ScrollToEnd()
        {
            Input.MousePosition.Returns(List.Rect.Center);
            Input.MouseWheelDelta.Returns(-100f);
            Frame();
            Input.MouseWheelDelta.Returns(0f);
            Frame();
        }

        internal void Save(string path)
        {
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            Assert.True(bitmap.GetPixel(110, 10).Alpha > 0,
                $"Button: {Button.Rect}; commands: {Button.DrawList.Count}; clip: {surface.Canvas.LocalClipBounds}");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }

        IEnumerable<LayoutNode> Nodes() => Descendants(Gui.RootNode!);
        static IEnumerable<LayoutNode> Descendants(LayoutNode node) =>
            new[] { node }.Concat(node.Children.SelectMany(Descendants));

        /// <inheritdoc />
        public void Dispose() => surface.Dispose();
    }

    /// <summary>The 32-slot dropdown clips to six rows and scrolling reaches the last layer.</summary>
    [Theory]
    [InlineData(nameof(Node.PhysicsLayer))]
    [InlineData(nameof(Node.RenderLayer))]
    public void LayerPickerClipsScrollsAndSelectsLastSlot(string member)
    {
        var node = new Node();
        using var host = new Harness(node, member);
        host.Open();
        var list = host.List;
        Assert.NotNull(host.Gui.GetScrollState(list.Id));
        Assert.True(list.Rect.H <= ThemeTokens.Current.Scale(ThemeTokens.Current.RowHeight) * 6 + 0.01f);
        Assert.True(list.Rect.BottomRight.Y <= 260);
        host.Save("/tmp/turian-layer-picker-top.png");
        host.ScrollToEnd();
        Assert.True(host.Gui.GetScrollState(list.Id)!.ScrollOffset.Y > 0,
            $"Offset: {host.Gui.GetScrollState(list.Id)!.ScrollOffset}; content: "
            + $"{host.Gui.GetScrollState(list.Id)!.ContentSize}; viewport: {host.List.Rect}");
        Assert.True(host.List.Rect.Contains(host.Option(31).Rect.Center),
            $"Viewport: {host.List.Rect}; last row: {host.Option(31).Rect}");
        host.Save("/tmp/turian-layer-picker-bottom.png");
        host.Click(host.Option(31).Rect.Center);
        Assert.Equal(31, (int)typeof(Node).GetProperty(member)!.GetValue(node)!);
    }

    /// <summary>A clipped row cannot receive a click outside the dropdown viewport.</summary>
    [Fact]
    public void OverflowRowsCannotReceiveClicks()
    {
        var node = new Node();
        using var host = new Harness(node, nameof(Node.RenderLayer));
        host.Open();
        var point = host.Option(7).Rect.Center;
        Assert.False(host.List.Rect.Contains(point));
        host.Click(point);
        Assert.Equal(0, node.RenderLayer);
    }

    /// <summary>Queued mask choices preserve all 32 bits and keep the checkbox popup open.</summary>
    [Fact]
    public void MaskPickerSelectsNothingEverythingAndIndividualBits()
    {
        var camera = new CameraComponent();
        using var host = new Harness(camera, nameof(CameraComponent.CullingMask));
        host.Open();
        host.ClickAction("clear");
        Assert.Equal(LayerMask.Nothing, camera.CullingMask);
        host.Click(host.Option(0).Rect.Center);
        Assert.Equal(LayerMask.FromLayer(0), camera.CullingMask);
        host.Click(host.Option(0).Rect.Center);
        Assert.Equal(LayerMask.Nothing, camera.CullingMask);
        host.ClickAction("all");
        Assert.Equal(LayerMask.Everything, camera.CullingMask);
        host.ScrollToEnd();
        host.Click(host.Option(31).Rect.Center);
        Assert.Equal(~LayerMask.FromLayer(31), camera.CullingMask);
    }
}
