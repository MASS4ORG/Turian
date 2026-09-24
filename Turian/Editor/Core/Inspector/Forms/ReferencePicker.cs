namespace Turian.Editor.Core;

/// <summary>One thing a reference field could be pointed at.</summary>
/// <param name="Id">The asset or node id to store.</param>
/// <param name="Name">Display name.</param>
/// <param name="Detail">Secondary line — the asset type, or the owning node's path.</param>
public readonly record struct ReferenceCandidate(Guid Id, string Name, string Detail);

/// <summary>
/// Answers what a <see cref="ReferenceField"/> may point at and what its current value is called,
/// by querying the asset catalog for asset references and the open scene for node and component
/// ones. Shared by the picker menu and by drag-and-drop validation, so both accept exactly the same
/// set.
/// </summary>
/// <param name="assets">The project catalog, for asset candidates.</param>
/// <param name="sceneTree">The open scene, for node and component candidates.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class ReferencePicker(AssetDatabase assets, SceneTreeController sceneTree)
{
    /// <summary>Everything <paramref name="field"/> could be pointed at, narrowed by a search.</summary>
    /// <param name="field">The field being edited.</param>
    /// <param name="search">Case-insensitive substring, or null for everything.</param>
    /// <returns>The candidates, in catalog or hierarchy order.</returns>
    public IReadOnlyList<ReferenceCandidate> Candidates(ReferenceField field, string? search = null)
    {
        ArgumentNullException.ThrowIfNull(field);

        var candidates = field.Kind switch
        {
            ReferenceKind.Asset => AssetCandidates(field.TargetType),
            ReferenceKind.Node => SceneCandidates(node => field.TargetType.IsInstanceOfType(node)),
            ReferenceKind.Component => SceneCandidates(HasComponent(field.TargetType)),
            _ => [],
        };

        return string.IsNullOrWhiteSpace(search)
            ? candidates
            : [.. candidates.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// What the field's current value should read as — the asset's or node's name, "None" when
    /// empty, and a missing marker when the id no longer resolves.
    /// </summary>
    /// <param name="field">The field being drawn.</param>
    /// <returns>The text for the value box.</returns>
    public string DisplayName(ReferenceField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.IsEmpty) return $"None ({field.TargetType.Name})";

        var name = field.Kind == ReferenceKind.Asset
            ? AssetName(field.CurrentId)
            : FindNode(field.CurrentId)?.Name;

        return name ?? $"Missing ({field.TargetType.Name})";
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a legal value for the field — the check behind both the
    /// picker list and a drop.
    /// </summary>
    /// <param name="field">The field being edited.</param>
    /// <param name="id">The candidate asset or node id.</param>
    /// <returns>True when the field may be pointed at it.</returns>
    public bool Accepts(ReferenceField field, Guid id)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (id == Guid.Empty) return false;

        return field.Kind switch
        {
            ReferenceKind.Asset => assets.TryGetAsset(id, out var record) && record is not null
                                   && AssetReferenceQuery.IsValidForAssetType(record, field.TargetType),
            ReferenceKind.Node => FindNode(id) is { } node && field.TargetType.IsInstanceOfType(node),
            ReferenceKind.Component => FindNode(id) is { } owner && HasComponent(field.TargetType)(owner),
            _ => false,
        };
    }

    static Func<Node, bool> HasComponent(Type componentType) =>
        node => node.Components.Any(componentType.IsInstanceOfType);

    static IEnumerable<Node> Descend(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descend(child))
                yield return descendant;
    }

    IReadOnlyList<ReferenceCandidate> AssetCandidates(Type targetType) =>
    [
        .. assets.GetAssetsSnapshot()
            .Where(record => AssetReferenceQuery.IsValidForAssetType(record, targetType))
            .Select(record => new ReferenceCandidate(
                record.AssetId,
                Path.GetFileNameWithoutExtension(record.SourceRelativePath),
                record.AssetTypeName))
    ];

    IReadOnlyList<ReferenceCandidate> SceneCandidates(Func<Node, bool> matches)
    {
        if (sceneTree.CurrentSceneRoot is not { } root) return [];

        return [.. Descend(root).Where(matches).Select(node => new ReferenceCandidate(node.Id, node.Name, NodePath(node)))];
    }

    /// <summary>Resolves a scene node by id, or null when the node is not in the open scene.</summary>
    /// <param name="id">The node id to resolve.</param>
    /// <returns>The matching node, or null.</returns>
    public Node? FindNode(Guid id) =>
        sceneTree.CurrentSceneRoot is { } root ? Node.FindById(root, id) : null;

    string? AssetName(Guid id) =>
        assets.TryGetAsset(id, out var record) && record is not null
            ? Path.GetFileNameWithoutExtension(record.SourceRelativePath)
            : null;

    static string NodePath(Node node)
    {
        var parts = new List<string>();
        for (var current = node.Parent; current is not null; current = current.Parent) parts.Insert(0, current.Name);
        return string.Join(" / ", parts);
    }
}
