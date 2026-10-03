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
readonly record struct DrawItem(
    Model Model,
    int SubMesh,
    MaterialResource Material,
    Matrix4x4 ModelMatrix,
    Matrix4x4 NormalMatrix,
    long SortKey,
    int Sequence = 0,
    Bounds WorldBounds = default);

/// <summary>
/// The scene content one frame draws: models and lights gathered in a single walk, and the submesh draws sorted so
/// consecutive draws share a material and model. Every collection is reused, so steady frames allocate nothing.
/// </summary>
sealed class RenderList
{
    // Weak keys, so a model or material released by a scene switch is not kept alive by its number.
    readonly ConditionalWeakTable<object, StrongBox<int>> stateOrder = [];
    int nextOrder;

    /// <summary>The active model components found by the last <see cref="Gather"/>.</summary>
    public List<ModelComponent> Models { get; } = [];

    /// <summary>The active light components found by the last <see cref="Gather"/>.</summary>
    public List<LightComponent> Lights { get; } = [];

    /// <summary>The submesh draws, in recording order after <see cref="Sort"/>.</summary>
    public List<DrawItem> Draws { get; } = [];

    /// <summary>Collects the active models and lights under <paramref name="roots"/>, depth-first.</summary>
    /// <param name="roots">The scene's top-level nodes.</param>
    public void Gather(IList<Node> roots)
    {
        Models.Clear();
        Lights.Clear();
        Draws.Clear();
        for (var i = 0; i < roots.Count; i++)
            Collect(roots[i]);
    }

    void Collect(Node node)
    {
        // An inactive node hides its whole subtree, as Unity's activeInHierarchy does.
        if (!node.IsActive) return;

        var components = node.Components;
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i] is ModelComponent { IsActive: true } model) Models.Add(model);
            else if (components[i] is LightComponent { IsActive: true } light) Lights.Add(light);
        }

        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
            Collect(children[i]);
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

    /// <summary>Orders <see cref="Draws"/> by material, then model, so recording rebinds as little as possible.</summary>
    public void Sort() =>
        CollectionsMarshal.AsSpan(Draws).Sort(static (a, b) =>
        {
            var order = a.SortKey.CompareTo(b.SortKey);
            return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
        });

    /// <summary>Forgets the material and model numbering, for when the resources behind them are released.</summary>
    public void Reset()
    {
        stateOrder.Clear();
        nextOrder = 0;
        Models.Clear();
        Lights.Clear();
        Draws.Clear();
    }
}
