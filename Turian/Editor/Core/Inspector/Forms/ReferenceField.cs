namespace Turian.Editor.Core;

/// <summary>What a reference field points at, which decides where its candidates come from.</summary>
public enum ReferenceKind
{
    /// <summary>An asset in the project catalog.</summary>
    Asset,

    /// <summary>A node in the open scene.</summary>
    Node,

    /// <summary>A component on a node in the open scene.</summary>
    Component,
}

/// <summary>
/// A <see cref="FormField"/> holding an <see cref="AssetReference{TAsset}"/>, <see cref="NodeRef{T}"/>
/// or <see cref="ComponentRef{T}"/>, seen as a single id plus the type it must point at. Lets one
/// drawer serve all three without reflecting over open generics itself. A member typed directly as a
/// <see cref="Node"/>, <see cref="Component"/> or <see cref="DataAsset"/> is a reference too (see <see cref="IsDirect"/>).
/// </summary>
public sealed class ReferenceField
{
    readonly FormField source;

    ReferenceField(FormField source, ReferenceKind kind, Type targetType, bool isDirect = false)
    {
        this.source = source;
        Kind = kind;
        TargetType = targetType;
        IsDirect = isDirect;
    }

    /// <summary>
    /// Whether the member holds the referenced object itself rather than an id wrapper; it is written with
    /// <see cref="SetTarget"/>.
    /// </summary>
    public bool IsDirect { get; }

    /// <summary>Which registry the reference resolves against.</summary>
    public ReferenceKind Kind { get; }

    /// <summary>The type a candidate must be assignable to.</summary>
    public Type TargetType { get; }

    /// <summary>The field's display label.</summary>
    public string Label => source.Label;

    /// <summary>Whether the member refuses writes.</summary>
    public bool IsReadOnly => source.IsReadOnly;

    /// <summary>The referenced asset or node id, or <see cref="Guid.Empty"/> when unset.</summary>
    public Guid CurrentId => source.GetValue() switch
    {
        null when IsDirect && source.Target is IdClass owner
                  && ObjectReferences.TryGetUnresolved(owner, source.Name, out var pending) => pending[0],
        null => Guid.Empty,
        Component component when IsDirect => component.Node?.Id ?? component.Id,
        IdClass value when IsDirect => value.Id,
        var value => IdOf(value),
    };

    /// <summary>Whether the reference points at nothing.</summary>
    public bool IsEmpty => CurrentId == Guid.Empty;

    /// <summary>
    /// Recognises a field whose type is one of the three reference generics, or null when it is not
    /// a reference at all.
    /// </summary>
    /// <param name="field">The field to classify.</param>
    /// <returns>A reference view over the field, or null.</returns>
    public static ReferenceField? TryCreate(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        // Typed subclasses (PrefabReference, DataAssetReference) are drawn as the AssetReference they extend.
        var type = field.ValueType;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AssetReference<>))
            {
                type = current;
                break;
            }
        }

        if (!type.IsGenericType) return TryCreateDirect(field);

        var definition = type.GetGenericTypeDefinition();
        var argument = type.GetGenericArguments()[0];

        if (definition == typeof(AssetReference<>)) return new ReferenceField(field, ReferenceKind.Asset, argument);
        if (definition == typeof(NodeRef<>)) return new ReferenceField(field, ReferenceKind.Node, argument);
        if (definition == typeof(ComponentRef<>)) return new ReferenceField(field, ReferenceKind.Component, argument);

        return null;
    }

    /// <summary>Whether the field holds one of the three reference generics.</summary>
    /// <param name="field">The field to classify.</param>
    /// <returns>True when <see cref="TryCreate"/> would succeed.</returns>
    public static bool IsReference(FormField field) => TryCreate(field) is not null;

    /// <summary>Points the reference at <paramref name="id"/>, replacing whatever it held.</summary>
    /// <param name="id">The asset or node id to reference.</param>
    /// <returns>True when the write succeeded.</returns>
    public bool Set(Guid id)
    {
        if (IsReadOnly) return false;

        // Assigning a fresh instance rather than mutating in place keeps undo and change notification
        // working the same way they do for every other field.
        var value = Activator.CreateInstance(source.ValueType);
        if (value is null) return false;

        SetIdOn(value, id);
        return source.SetValue(value);
    }

    /// <summary>
    /// Points a direct reference at <paramref name="target"/>, replacing whatever it held, including an id
    /// whose target was missing.
    /// </summary>
    /// <param name="target">The object to reference, or null to clear.</param>
    /// <returns>True when the write succeeded.</returns>
    public bool SetTarget(object? target)
    {
        if (IsReadOnly || (target is not null && !TargetType.IsInstanceOfType(target))) return false;
        if (source.Target is IdClass owner) ObjectReferences.Forget(owner, source.Name);
        return source.SetValue(target);
    }

    /// <summary>Clears the reference.</summary>
    /// <returns>True when the write succeeded.</returns>
    public bool Clear() => IsDirect ? SetTarget(null) : Set(Guid.Empty);

    static ReferenceField? TryCreateDirect(FormField field)
    {
        var type = field.ValueType;
        if (!ObjectReferences.IsReferenceMember(type, allowSceneObjects: true) || type.IsArray || type.IsGenericType)
            return null;

        var kind = typeof(DataAsset).IsAssignableFrom(type) ? ReferenceKind.Asset
            : typeof(Component).IsAssignableFrom(type) ? ReferenceKind.Component
            : ReferenceKind.Node;
        return new ReferenceField(field, kind, type, isDirect: true);
    }

    Guid IdOf(object value)
    {
        var name = Kind == ReferenceKind.Asset ? nameof(AssetReference<Asset>.AssetId) : nameof(NodeRef<Node>.NodeId);
        return value.GetType().GetProperty(name)?.GetValue(value) is Guid id ? id : Guid.Empty;
    }

    void SetIdOn(object value, Guid id)
    {
        var name = Kind == ReferenceKind.Asset ? nameof(AssetReference<Asset>.AssetId) : nameof(NodeRef<Node>.NodeId);
        value.GetType().GetProperty(name)?.SetValue(value, id);
    }
}
