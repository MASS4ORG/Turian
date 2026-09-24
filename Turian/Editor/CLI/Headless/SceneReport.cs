namespace Turian.Editor.CLI;

/// <summary>
/// A walk over a loaded hierarchy: node and component counts, how many model references resolve,
/// and the world-space bounds of everything that draws.
/// </summary>
sealed class SceneReport
{
    /// <summary>Gets the number of nodes in the hierarchy.</summary>
    public int NodeCount { get; private init; }

    /// <summary>Gets the number of nodes whose <see cref="Node.IsActive"/> is set.</summary>
    public int ActiveNodeCount { get; private init; }

    /// <summary>Gets the component count per component type name.</summary>
    public IReadOnlyDictionary<string, int> ComponentCounts { get; private init; } = new Dictionary<string, int>();

    /// <summary>Gets the number of <see cref="ModelComponent"/>s whose mesh asset resolved.</summary>
    public int ResolvedMeshCount { get; private init; }

    /// <summary>Gets the number of <see cref="ModelComponent"/>s whose mesh asset did not resolve.</summary>
    public int UnresolvedMeshCount { get; private init; }

    /// <summary>Gets the number of <see cref="LightComponent"/>s that will light the scene.</summary>
    public int LightCount { get; private init; }

    /// <summary>Gets the world-space bounds of every mesh in the hierarchy.</summary>
    public Bounds Bounds { get; private init; } = Bounds.Empty;

    /// <summary>
    /// Walks <paramref name="root"/> and collects the report.
    /// </summary>
    /// <param name="root">The root of the hierarchy.</param>
    /// <param name="loadModels">
    /// Whether to upload each model to the GPU while walking, which reports load failures and
    /// covers components that reference a whole model rather than one of its meshes.
    /// Requires a Vulkan device in <see cref="RuntimeServices"/>.
    /// </param>
    /// <returns>The collected report.</returns>
    public static SceneReport Collect(Node root, bool loadModels = false)
    {
        ArgumentNullException.ThrowIfNull(root);

        var nodes = 0;
        var activeNodes = 0;
        var components = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolved = 0;
        var unresolved = 0;
        var lights = 0;
        var bounds = Bounds.Empty;

        void Walk(Node node)
        {
            nodes++;
            if (node.IsActive) activeNodes++;

            foreach (var component in node.Components)
            {
                var name = component.GetType().Name;
                components[name] = components.GetValueOrDefault(name) + 1;

                if (component is LightComponent) lights++;
                if (component is not ModelComponent model) continue;

                var local = ModelBoundsUtility.ComputeLocalBounds(model, loadModels, out var isResolved);
                if (isResolved) resolved++;
                else unresolved++;

                if (!local.IsEmpty)
                {
                    bounds = bounds.Encapsulate(ModelBoundsUtility.ToWorldBounds(local, node.GlobalTransform.Matrix4X4()));
                }
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        Walk(root);

        return new SceneReport
        {
            NodeCount = nodes,
            ActiveNodeCount = activeNodes,
            ComponentCounts = components,
            ResolvedMeshCount = resolved,
            UnresolvedMeshCount = unresolved,
            LightCount = lights,
            Bounds = bounds,
        };
    }

    /// <summary>
    /// Writes the report through <paramref name="logger"/>.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    public void Write(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogInformation(
            "Scene: {NodeCount} nodes ({ActiveNodeCount} active), {LightCount} lights",
            NodeCount,
            ActiveNodeCount,
            LightCount);

        foreach (var (name, count) in ComponentCounts.OrderByDescending(static entry => entry.Value))
        {
            logger.LogInformation("  {ComponentName}: {Count}", name, count);
        }

        logger.LogInformation(
            "Meshes: {ResolvedMeshCount} resolved, {UnresolvedMeshCount} unresolved",
            ResolvedMeshCount,
            UnresolvedMeshCount);

        if (Bounds.IsEmpty)
        {
            logger.LogInformation("Bounds: none — nothing in this scene draws geometry");
            return;
        }

        logger.LogInformation(
            "Bounds: min ({MinX:F2}, {MinY:F2}, {MinZ:F2}) max ({MaxX:F2}, {MaxY:F2}, {MaxZ:F2}) "
            + "size ({SizeX:F2}, {SizeY:F2}, {SizeZ:F2})",
            Bounds.Min.X, Bounds.Min.Y, Bounds.Min.Z,
            Bounds.Max.X, Bounds.Max.Y, Bounds.Max.Z,
            Bounds.Size.X, Bounds.Size.Y, Bounds.Size.Z);
    }

}
