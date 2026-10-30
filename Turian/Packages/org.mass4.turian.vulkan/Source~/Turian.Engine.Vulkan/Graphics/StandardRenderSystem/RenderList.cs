namespace Turian.Engine.Core;

/// <summary>One submesh draw: the model and material to bind, its matrices, and the key it is sorted by.</summary>
/// <param name="Model">The model whose buffers are bound.</param>
/// <param name="SubMesh">The submesh index within <paramref name="Model"/>.</param>
/// <param name="Material">The material descriptor set bound at set 1.</param>
/// <param name="ModelMatrix">The world matrix of the owning node.</param>
/// <param name="NormalMatrix">The normal matrix of the owning node.</param>
/// <param name="SortKey">Material order in the high bits, model order in the low bits.</param>
/// <param name="Sequence">Scene order, used to preserve depth and blend ordering within equal sort keys.</param>
/// <param name="WorldBounds">The world-space bounds supplied to optional visibility passes.</param>
/// <param name="RenderLayer">The owning node's rendering layer index.</param>
/// <param name="Color">The per-instance tint multiplied into the base color.</param>
/// <param name="CustomData">Free per-instance values passed to the shader.</param>
readonly record struct DrawItem(
    VulkanModel Model,
    int SubMesh,
    MaterialResource Material,
    Matrix4x4 ModelMatrix,
    Matrix4x4 NormalMatrix,
    long SortKey,
    int Sequence = 0,
    Bounds WorldBounds = default,
    int RenderLayer = 0,
    Color Color = default,
    Vector4 CustomData = default);

/// <summary>
/// The scene content one frame draws: models and lights gathered from a structural index, and the submesh draws sorted so
/// consecutive draws share a material and model. Every collection is reused, so steady frames allocate nothing.
/// </summary>
sealed class RenderList
{
    static readonly SceneIndex.Category Renderables = new(static node =>
    {
        foreach (var component in node.Components)
        {
            if (component is ModelComponent or InstancedModelComponent or LightComponent) { return true; }
        }

        return false;
    });

    // Weak keys, so a model or material released by a scene switch is not kept alive by its number.
    readonly ConditionalWeakTable<object, StrongBox<int>> stateOrder = [];
    int nextOrder;

    /// <summary>The active model components found by the last <see cref="Gather"/>.</summary>
    public List<ModelComponent> Models { get; } = [];

    /// <summary>The active light components found by the last <see cref="Gather"/>.</summary>
    public List<LightComponent> Lights { get; } = [];

    /// <summary>The active instanced model components found by the last <see cref="Gather"/>.</summary>
    public List<InstancedModelComponent> InstancedModels { get; } = [];

    /// <summary>The submesh draws, in recording order after <see cref="Sort"/>.</summary>
    public List<DrawItem> Draws { get; } = [];

    /// <summary>Collects the active models, instances and lights under <paramref name="roots"/>, depth-first.</summary>
    /// <param name="roots">The scene's top-level nodes.</param>
    /// <param name="cullingMask">Rendering layers accepted by the camera; null includes all layers.</param>
    public void Gather(IList<Node> roots, LayerMask? cullingMask = null)
    {
        Models.Clear();
        Lights.Clear();
        InstancedModels.Clear();
        Draws.Clear();
        for (var i = 0; i < roots.Count; i++)
        {
            Collect(roots[i], cullingMask ?? LayerMask.Everything);
        }
    }

    void Collect(Node root, LayerMask cullingMask)
    {
        if (!root.IsActive || root.IsDestroyed)
        {
            return;
        }

        foreach (var node in root.EnsureSceneIndex().Nodes(Renderables))
        {
            if (!node.IsActiveUnder(root) || !cullingMask.Contains(node.RenderLayer)) { continue; }
            var components = node.Components;
            for (var i = 0; i < components.Count; i++)
            {
                Add(components[i]);
            }
        }
    }

    void Add(Component component)
    {
        if (!component.IsActive)
        {
            return;
        }

        switch (component)
        {
            case ModelComponent model:
                Models.Add(model);
                break;
            case LightComponent light:
                Lights.Add(light);
                break;
            case InstancedModelComponent instanced:
                InstancedModels.Add(instanced);
                break;
        }
    }

    /// <summary>
    /// Gives a material or model a stable small number, in first-seen order, for building <see cref="DrawItem.SortKey"/>.
    /// </summary>
    /// <param name="state">The material or model.</param>
    /// <returns>The same number for the same object every frame.</returns>
    public int OrderOf(object state)
    {
        if (!stateOrder.TryGetValue(state, out var order))
        {
            order = new StrongBox<int>(nextOrder++);
            stateOrder.Add(state, order);
        }

        return order.Value;
    }

    /// <summary>Builds a sort key that groups draws by material first, then by model.</summary>
    /// <param name="materialOrder">The material's <see cref="OrderOf"/> number.</param>
    /// <param name="modelOrder">The model's <see cref="OrderOf"/> number.</param>
    /// <returns>The combined key.</returns>
    public static long SortKey(int materialOrder, int modelOrder) => ((long)materialOrder << 32) | (uint)modelOrder;

    /// <summary>
    /// Orders <see cref="Draws"/> by material, model, then submesh, so recording rebinds as little as possible and
    /// draws of the same submesh are contiguous and can merge into one instanced draw.
    /// </summary>
    public void Sort()
    {
        var draws = CollectionsMarshal.AsSpan(Draws);
        for (var i = 1; i < draws.Length; i++)
        {
            if (Compare(in draws[i - 1], in draws[i]) > 0)
            {
                draws.Sort(static (a, b) => Compare(in a, in b));
                return;
            }
        }
    }

    static int Compare(in DrawItem a, in DrawItem b)
    {
        var order = a.SortKey.CompareTo(b.SortKey);
        if (order == 0)
        {
            order = a.SubMesh.CompareTo(b.SubMesh);
        }

        return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
    }

    /// <summary>Counts the draws from <paramref name="start"/> that share its model, submesh and material.</summary>
    /// <param name="draws">The sorted draws.</param>
    /// <param name="start">The first draw of the run.</param>
    /// <returns>The run length, at least one.</returns>
    public static int RunLength(ReadOnlySpan<DrawItem> draws, int start)
    {
        ref readonly var first = ref draws[start];
        var end = start + 1;
        while (end < draws.Length
               && ReferenceEquals(draws[end].Model, first.Model)
               && draws[end].SubMesh == first.SubMesh
               && ReferenceEquals(draws[end].Material, first.Material))
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Forgets the material and model numbering, for when the resources behind them are released.</summary>
    public void Reset()
    {
        stateOrder.Clear();
        nextOrder = 0;
        Models.Clear();
        Lights.Clear();
        InstancedModels.Clear();
        Draws.Clear();
    }
}
