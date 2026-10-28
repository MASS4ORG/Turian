namespace Turian.Engine.Core;

/// <summary>Closed node callback and service-injection metadata supplied by the source generator.</summary>
/// <param name="HasFrameCallbacks">Whether the node overrides a frame callback.</param>
/// <param name="Inject">Compiled service injection for this node type.</param>
public sealed record GeneratedNodeMetadata(bool HasFrameCallbacks, Action<Node, IServiceProvider, bool> Inject)
{
    static readonly ConcurrentDictionary<Type, GeneratedNodeMetadata> Nodes = new();

    /// <summary>Registers metadata for a closed node type.</summary>
    public static void Register<T>(GeneratedNodeMetadata metadata) where T : Node
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Nodes[typeof(T)] = metadata;
    }

    /// <summary>Runs compiled injection for a known node base type.</summary>
    public static void InjectBase<T>(Node node, IServiceProvider services, bool allowMissingServices) where T : Node
    {
        if (Nodes.TryGetValue(typeof(T), out var metadata))
        {
            metadata.Inject(node, services, allowMissingServices);
        }
    }

    internal static bool TryGet(Type type,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GeneratedNodeMetadata? metadata) =>
        Nodes.TryGetValue(type, out metadata);

    internal static void ReleaseCollectible() => CollectibleAssemblies.RemoveReleased(Nodes, static type => type);
}
