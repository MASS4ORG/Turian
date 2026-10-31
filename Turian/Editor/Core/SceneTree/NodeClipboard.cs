namespace Turian.Editor.Core;

/// <summary>
/// The JSON clipboard format for scene objects. Nodes travel as a versioned envelope holding their serialized
/// hierarchies, and components as an envelope holding one serialized component, so a copy can be pasted into
/// another scene, another prefab, or another Studio window.
/// </summary>
public static class NodeClipboard
{
    /// <summary>The envelope kind written for a copied node hierarchy.</summary>
    public const string NodesKind = "turian.nodes";

    /// <summary>The envelope kind written for a copied component.</summary>
    public const string ComponentKind = "turian.component";

    const string KindMember = "kind";
    const string VersionMember = "version";
    const string NodesMember = "nodes";
    const string ComponentMember = "component";
    const int CurrentVersion = 1;

    /// <summary>Serializes <paramref name="nodes"/> as one clipboard envelope.</summary>
    /// <param name="nodes">The top-level nodes to copy; their descendants travel with them.</param>
    /// <returns>The envelope JSON, ready for the system clipboard.</returns>
    public static string WriteNodes(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var envelope = new JsonObject
        {
            [KindMember] = NodesKind,
            [VersionMember] = CurrentVersion,
            [NodesMember] = new JsonArray([.. nodes.Select(static node => JsonNode.Parse(Serializer.Serialize(node)))]),
        };
        return envelope.ToJsonString();
    }

    /// <summary>Reads nodes from a clipboard envelope, keeping their original ids.</summary>
    /// <param name="text">The clipboard text.</param>
    /// <param name="loader">Resolves DataAsset references, when the caller has one.</param>
    /// <param name="nodes">The parsed top-level nodes; empty when the text is not a node envelope.</param>
    /// <returns>True when at least one node was read.</returns>
    public static bool TryReadNodes(string? text, IAssetLoader? loader, out IReadOnlyList<Node> nodes)
    {
        nodes = [];
        if (!TryParse(text, NodesKind, out var envelope) || envelope[NodesMember] is not JsonArray array)
        {
            return false;
        }

        var parsed = new List<Node>(array.Count);
        foreach (var item in array)
        {
            if (item is null)
            {
                continue;
            }

            try
            {
                if (Serializer.LoadData<Node>(item.ToJsonString(), loader) is { } node)
                {
                    node.Parent = null;
                    parsed.Add(node);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                Log.Logger.LogWarning(exception, "Could not read a node from the clipboard");
            }
        }

        nodes = parsed;
        return parsed.Count > 0;
    }

    /// <summary>Serializes <paramref name="component"/> as one clipboard envelope.</summary>
    /// <param name="component">The component whose values are copied.</param>
    /// <returns>The envelope JSON, ready for the system clipboard.</returns>
    public static string WriteComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);

        return new JsonObject
        {
            [KindMember] = ComponentKind,
            [VersionMember] = CurrentVersion,
            [ComponentMember] = JsonNode.Parse(Serializer.Serialize(component)),
        }.ToJsonString();
    }

    /// <summary>Reads a component from a clipboard envelope.</summary>
    /// <param name="text">The clipboard text.</param>
    /// <param name="loader">Resolves DataAsset references, when the caller has one.</param>
    /// <param name="component">The parsed component, or null when the text is not a component envelope.</param>
    /// <returns>True when a usable component was read.</returns>
    public static bool TryReadComponent(string? text, IAssetLoader? loader, out Component? component)
    {
        component = null;
        if (!TryParse(text, ComponentKind, out var envelope) || envelope[ComponentMember] is not { } value)
        {
            return false;
        }

        try
        {
            component = Serializer.LoadData<Component>(value.ToJsonString(), loader);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            Log.Logger.LogWarning(exception, "Could not read a component from the clipboard");
            return false;
        }

        return component is not null and not MissingComponent;
    }

    /// <summary>Whether <paramref name="text"/> is a node clipboard envelope, without reading the nodes.</summary>
    /// <param name="text">The clipboard text.</param>
    /// <returns>True when the text holds a node hierarchy.</returns>
    public static bool HoldsNodes(string? text) => TryParse(text, NodesKind, out _);

    /// <summary>Whether <paramref name="text"/> is a component clipboard envelope, without reading the component.</summary>
    /// <param name="text">The clipboard text.</param>
    /// <returns>True when the text holds a component.</returns>
    public static bool HoldsComponent(string? text) => TryParse(text, ComponentKind, out _);

    static bool TryParse(string? text, string kind, out JsonObject envelope)
    {
        envelope = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject root
                || root[KindMember]?.GetValue<string>() != kind)
            {
                return false;
            }

            envelope = root;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                              or FormatException)
        {
            return false;
        }
    }
}
