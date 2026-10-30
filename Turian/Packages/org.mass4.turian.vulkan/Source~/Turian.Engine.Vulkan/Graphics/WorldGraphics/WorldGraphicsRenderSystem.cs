namespace Turian.Engine.Core;

/// <summary>Draws depth-tested, alpha-blended line/trail ribbons and Skia SDF text for one camera.</summary>
public sealed partial class WorldGraphicsRenderSystem : IRenderSystem
{
    static readonly SceneIndex.Category Graphics = new(HasGraphic);
    readonly Vulkan vulkan;
    readonly AssetDatabase assets;
    readonly Dictionary<Component, Entry> entries = [];
    readonly List<Entry> draws = [];
    readonly List<Component> stale = [];
    readonly RetiredResources retired = new();
    readonly Texture white;
    readonly DescriptorSetLayout textureLayout;
    PipelineLayout pipelineLayout;
    StandardPipeline pipeline = null!;
    long frame;

    sealed class Entry
    {
        internal long Frame;
        internal WorldGraphicGeometry Geometry = WorldGraphicGeometry.Empty;
        internal WorldGraphicMesh? Mesh;
        internal WorldGraphicBinding? Binding;
        internal string? Text;
        internal string? Font;
        internal SdfTextLayout? Layout;
        internal int Sdf;
        internal float Distance;
    }

    /// <summary>Creates renderer-owned caches and pipelines; no component owns backend resources.</summary>
    public WorldGraphicsRenderSystem(Vulkan vulkan, AssetDatabase assets, RenderPass renderPass,
        DescriptorSetLayout globalLayout)
    {
        this.vulkan = vulkan;
        this.assets = assets;
        white = new Texture(vulkan, 1, 1, [255, 255, 255, 255], false);
        textureLayout = new DescriptorSetLayoutBuilder(vulkan.Vk, vulkan.Device)
            .AddBinding(0, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit).Build();
        CreatePipelineLayout(globalLayout);
        CreatePipeline(renderPass);
    }

    /// <inheritdoc/>
    public void Prepare(FrameInfo frameInfo, GlobalUbo ubo)
    {
        frame++;
        retired.NextFrame();
        draws.Clear();
        foreach (var node in frameInfo.Nodes)
        {
            Collect(node, frameInfo.Camera);
        }
        stale.Clear();
        foreach (var (component, entry) in entries)
        {
            if (entry.Frame != frame)
            {
                stale.Add(component);
            }
        }
        foreach (var component in stale)
        {
            var entry = entries[component];
            retired.Retire(entry.Mesh);
            retired.Retire(entry.Binding);
            entries.Remove(component);
        }
        draws.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));
    }

    static bool HasGraphic(Node node)
    {
        foreach (var component in node.Components)
        {
            if (component is LineRenderer or WorldTextComponent)
            {
                return true;
            }
        }
        return false;
    }

    void Collect(Node root, ICamera camera)
    {
        if (!root.IsActive || root.IsDestroyed)
        {
            return;
        }
        foreach (var node in root.EnsureSceneIndex().Nodes(Graphics))
        {
            if (node.IsActiveUnder(root) && camera.CullingMask.Contains(node.RenderLayer))
            {
                CollectComponents(node, camera);
            }
        }
    }

    void CollectComponents(Node node, ICamera camera)
    {
        foreach (var component in node.Components)
        {
            if (component.IsActive && !component.IsDestroyed)
            {
                PrepareComponent(component, camera);
            }
        }
    }

    void PrepareComponent(Component component, ICamera camera)
    {
        if (component is not LineRenderer && component is not WorldTextComponent)
        {
            return;
        }
        if (!entries.TryGetValue(component, out var entry))
        {
            entries[component] = entry = new();
        }
        entry.Frame = frame;
        var geometry = ComponentGeometry(component, entry, camera);
        SetGeometry(entry, geometry);
        if (entry.Mesh is null || geometry.Indices.Length == 0)
        {
            return;
        }
        entry.Distance = Vector3.DistanceSquared(camera.Position, Center(geometry));
        draws.Add(entry);
    }

    WorldGraphicGeometry ComponentGeometry(Component component, Entry entry, ICamera camera)
    {
        if (component is LineRenderer line)
        {
            var texture = line.Texture is { IsEmpty: false } reference
                ? new TextureAsset { Id = reference.AssetId }.GetContent(vulkan, assets) ?? white : white;
            SetBinding(entry, texture, false);
            return line.BuildGeometry(camera);
        }
        var text = (WorldTextComponent)component;
        if (string.IsNullOrEmpty(text.Text))
        {
            return WorldGraphicGeometry.Empty;
        }
        PrepareText(entry, text);
        return text.BuildGeometry(camera, entry.Layout!.EmSize * text.FontSize);
    }

    void PrepareText(Entry entry, WorldTextComponent text)
    {
        if (entry.Text == text.Text && entry.Font == text.Font)
        {
            return;
        }
        var layout = SdfTextRasterizer.Rasterize(text.Text, text.Font, assets);
        var texture = new Texture(vulkan, (uint)layout.Width, (uint)layout.Height, layout.Rgba, false,
            addressMode: SamplerAddressMode.ClampToEdge);
        SetBinding(entry, texture, true);
        entry.Text = text.Text;
        entry.Font = text.Font;
        entry.Layout = layout;
        entry.Sdf = 1;
    }

    void SetBinding(Entry entry, Texture texture, bool owned)
    {
        if (ReferenceEquals(entry.Binding?.Texture, texture))
        {
            return;
        }
        var binding = new WorldGraphicBinding(vulkan, textureLayout, texture, owned);
        retired.Retire(entry.Binding);
        entry.Binding = binding;
    }

    void SetGeometry(Entry entry, WorldGraphicGeometry geometry)
    {
        if (entry.Geometry.Vertices.AsSpan().SequenceEqual(geometry.Vertices)
            && entry.Geometry.Indices.AsSpan().SequenceEqual(geometry.Indices))
        {
            return;
        }
        var mesh = geometry.Indices.Length > 0 ? new WorldGraphicMesh(vulkan, geometry) : null;
        retired.Retire(entry.Mesh);
        entry.Mesh = mesh;
        entry.Geometry = geometry;
    }

    static Vector3 Center(WorldGraphicGeometry geometry)
    {
        var center = Vector3.Zero;
        foreach (var vertex in geometry.Vertices)
        {
            center += vertex.Position / geometry.Vertices.Length;
        }
        return center;
    }

    /// <inheritdoc/>
    public unsafe void Render(FrameInfo frameInfo, ref GlobalUbo ubo)
    {
        if (draws.Count == 0)
        {
            return;
        }
        pipeline.Bind(frameInfo.CommandBuffer);
        var global = frameInfo.GlobalDescriptorSet;
        vulkan.Vk.CmdBindDescriptorSets(frameInfo.CommandBuffer, PipelineBindPoint.Graphics,
            pipelineLayout, 0, 1, in global, 0, null);
        foreach (var entry in draws)
        {
            var set = entry.Binding!.Set;
            vulkan.Vk.CmdBindDescriptorSets(frameInfo.CommandBuffer, PipelineBindPoint.Graphics,
                pipelineLayout, 1, 1, in set, 0, null);
            var sdf = entry.Sdf;
            vulkan.Vk.CmdPushConstants(frameInfo.CommandBuffer, pipelineLayout, ShaderStageFlags.FragmentBit,
                0, sizeof(int), ref sdf);
            entry.Mesh!.Draw(frameInfo.CommandBuffer);
        }
    }

    /// <summary>Releases per-view resources after the host has waited for the device to become idle.</summary>
    public unsafe void Dispose()
    {
        foreach (var entry in entries.Values)
        {
            entry.Mesh?.Dispose();
            entry.Binding?.Dispose();
        }
        entries.Clear();
        draws.Clear();
        retired.Dispose();
        pipeline.Dispose();
        vulkan.Vk.DestroyPipelineLayout(vulkan.Device.VkDevice, pipelineLayout, null);
        textureLayout.Dispose();
        white.Dispose();
    }
}
