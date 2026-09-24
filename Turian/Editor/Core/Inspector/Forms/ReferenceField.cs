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
/// drawer serve all three without reflecting over open generics itself.
/// </summary>
public sealed class ReferenceField
{
    readonly FormField source;

    ReferenceField(FormField source, ReferenceKind kind, Type targetType)
    {
        this.source = source;
        Kind = kind;
        TargetType = targetType;
    }

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
        null => Guid.Empty,
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

        var type = field.ValueType;
        if (!type.IsGenericType) return null;

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

    /// <summary>Clears the reference.</summary>
    /// <returns>True when the write succeeded.</returns>
    public bool Clear() => Set(Guid.Empty);

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
