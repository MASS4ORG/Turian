namespace Turian.Engine.Core;

/// <summary>Marks a class or record whose TypeId identifies a code-authored layer group.</summary>
public interface ILayerGroupKey;

/// <summary>Marks a class or record whose TypeId identifies a value belonging to a specific group.</summary>
public interface ILayerValueKey<TGroup> where TGroup : class, ILayerGroupKey;

/// <summary>Marks a class or record whose TypeId identifies a code-authored tag.</summary>
public interface ITagKey;

static class LayerKeyIdentity<T> where T : class
{
    static readonly Lazy<Guid> Identity = new(() => TypeRegistry.GetIdOrThrow(typeof(T)));

    internal static Guid Id => Identity.Value;
}
