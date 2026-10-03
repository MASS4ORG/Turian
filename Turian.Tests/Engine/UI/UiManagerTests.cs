namespace Turian.Tests;

/// <summary>
/// Covers <see cref="UiManager"/>'s world-space panels and <c>.ui</c> document panels on a headless Vulkan device.
/// Skipped when no usable Vulkan device.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class UiManagerTests : IClassFixture<VulkanFixture>, IDisposable
{
    readonly VulkanFixture fixture;
    readonly string directory = Directory.CreateTempSubdirectory("turian-ui-").FullName;
    readonly AssetDatabase database;

    /// <summary>Receives the shared headless Vulkan device fixture.</summary>
    public UiManagerTests(VulkanFixture fixture)
    {
        this.fixture = fixture;
        TestAssetDatabase.Reset();
        UiDocumentAsset.ClearCache();
        database = new AssetDatabase();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TestAssetDatabase.Reset();
        UiDocumentAsset.ClearCache();
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>A controller the test document binds, counting its updates.</summary>
    public sealed class CountingController : UiController
    {
        /// <summary>How many frames ticked this controller.</summary>
        public static int Updates { get; set; }

        /// <inheritdoc />
        public override void OnUpdate(float deltaTime) => Updates++;
    }

    static (Node Root, UiDocumentComponent Panel) Scene(UiDocumentComponent panel, params Component[] extra)
    {
        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Panel" };
        child.AddComponent(panel);
        foreach (var component in extra) child.AddComponent(component);
        child.Parent = root;
        root.Children.Add(child);
        root.Awake(null);
        return (root, panel);
    }

    static WorldUiFrame Frame()
    {
        var camera = Substitute.For<ICamera>();
        camera.GetViewMatrix().Returns(Matrix4x4.Identity);
        camera.GetProjectionMatrix().Returns(Matrix4x4.Identity);
        return new WorldUiFrame(camera, 256, 256, 0.016f);
    }

    Guid RegisterDocument(string xml)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "Assets")).FullName, "panel.ui");
        File.WriteAllText(path, xml);
        Assert.True(database.RegisterAsset(new UiDocumentAsset { Id = id, RelativePath = path }, path));
        return id;
    }

    /// <summary>Each drawable world-space panel becomes a quad; screen-space and inactive panels do not.</summary>
    [Fact]
    public void RenderWorldPanels_RendersEachActiveWorldPanel()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var manager = new UiManager(fixture.Vulkan);
        var (root, panel) = Scene(
            new UiDocumentComponent { Mode = UiRenderMode.WorldSpace, PanelSize = new Int2(64, 32), OnBuild = _ => { } },
            new UiRaycasterComponent { MaxDistance = 5f });
        var screen = new UiDocumentComponent { OnBuild = _ => { } };
        root.AddComponent(screen);

        var quads = manager.RenderWorldPanels(root, Frame());
        Assert.Single(quads);
        Assert.Single(manager.RenderWorldPanels(root, Frame()));

        panel.IsActive = false;
        Assert.Empty(manager.RenderWorldPanels(root, Frame()));
    }

    /// <summary>A <c>.ui</c> document panel resolves once, binds its controller and ticks it every frame.</summary>
    [Fact]
    public void RenderOverlay_ResolvesDocumentPanels()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var documentId = RegisterDocument($"""
            <UI xmlns="https://turian.mass4.org/ui" controller="{typeof(CountingController).FullName}">
              <Style src="missing.uss" />
              <Label text="Hello" />
            </UI>
            """);
        using var manager = new UiManager(fixture.Vulkan);
        var (root, _) = Scene(new UiDocumentComponent { Document = new AssetReference<UiDocumentAsset>(documentId) });
        CountingController.Updates = 0;

        Assert.NotNull(manager.RenderOverlay(root, 128, 64, 0.016f));
        Assert.NotNull(manager.RenderOverlay(root, 128, 64, 0.016f));
        Assert.Equal(2, CountingController.Updates);
    }

    /// <summary>A document reference that resolves to nothing draws nothing.</summary>
    [Fact]
    public void RenderOverlay_UnresolvedDocumentDrawsNothing()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        using var manager = new UiManager(fixture.Vulkan);
        var (root, _) = Scene(new UiDocumentComponent
        {
            Mode = UiRenderMode.WorldSpace,
            Document = new AssetReference<UiDocumentAsset>(Guid.NewGuid()),
        });

        Assert.Empty(manager.RenderWorldPanels(root, Frame()));
    }
}
