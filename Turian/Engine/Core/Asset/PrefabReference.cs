namespace Turian.Engine.Core;

/// <summary>
/// Typed reference to a <see cref="Prefab"/> whose root hierarchy is expected to contain
/// a component of type <typeparamref name="TComponent"/>. The generic parameter is a
/// compile-time guarantee for callers and a hint for the editor's prefab picker; the
/// serialized form is identical to <see cref="AssetReference{Prefab}"/> — just the asset id.
/// </summary>
/// <typeparam name="TComponent">The component type the referenced prefab is expected to expose.</typeparam>
public sealed class PrefabReference<TComponent> : AssetReference<Prefab>
    where TComponent : Component
{
    /// <summary>
    /// Initializes a new empty prefab reference.
    /// </summary>
    public PrefabReference()
    {
    }

    /// <summary>
    /// Initializes a new prefab reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The referenced asset identifier.</param>
    public PrefabReference(Guid assetId)
        : base(assetId)
    {
    }

    /// <summary>
    /// Initializes a new prefab reference pointing to the specified prefab.
    /// </summary>
    /// <param name="prefab">The referenced prefab asset.</param>
    public PrefabReference(Prefab prefab)
        : base(prefab)
    {
    }

    /// <summary>
    /// Instantiates the referenced prefab through the provided scene manager and returns
    /// the first <typeparamref name="TComponent"/> found on the instantiated root or its children.
    /// </summary>
    /// <param name="sceneManager">The scene manager used to resolve and instantiate the prefab.</param>
    /// <param name="parent">Optional parent node. When null, the active scene root is used.</param>
    /// <returns>The first matching component on the instantiated hierarchy.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sceneManager"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the reference is empty or the instantiated hierarchy does not contain
    /// a component of type <typeparamref name="TComponent"/>.
    /// </exception>
    public async Task<TComponent> InstantiateAsync(ISceneManager sceneManager, Node? parent = null)
    {
        ArgumentNullException.ThrowIfNull(sceneManager);

        if (IsEmpty)
        {
            throw new InvalidOperationException(
                $"Cannot instantiate {nameof(PrefabReference<TComponent>)} because it is empty.");
        }

        var root = await sceneManager.InstantiateAsync(this, parent);

        var component = root.GetComponent<TComponent>();
        if (component is not null)
        {
            return component;
        }

        foreach (var found in Node.GetComponentsInChildren<TComponent>(root))
        {
            return found;
        }

        throw new InvalidOperationException(
            $"Prefab '{AssetId}' does not contain a component of type '{typeof(TComponent).FullName}'.");
    }

    /// <summary>
    /// Creates a prefab reference pointing to the specified prefab.
    /// </summary>
    /// <param name="prefab">The prefab to reference.</param>
    /// <returns>A new prefab reference.</returns>
    public new static PrefabReference<TComponent> From(Prefab prefab) => new(prefab);

    /// <summary>
    /// Creates a prefab reference pointing to the specified asset identifier.
    /// </summary>
    /// <param name="assetId">The asset identifier to reference.</param>
    /// <returns>A new prefab reference.</returns>
    public new static PrefabReference<TComponent> From(Guid assetId) => new(assetId);
}
