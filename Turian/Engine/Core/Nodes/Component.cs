namespace Turian.Engine.Core;

/// <summary>
/// Base class for all components that can be attached to nodes in the scene graph.
/// </summary>
public abstract class Component : IdClass
{
    /// <summary>Gets or sets a value indicating whether this component is active.</summary>
    [HideInEditor]
    public bool IsActive
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (IsAttached)
            {
                if (value) OnEnable();
                else OnDisable();
            }
        }
    } = true;

    /// <summary>Gets the node this component is attached to.</summary>
    [JsonIgnore, HideInEditor]
    public Node? Node { get; private set; }

    /// <summary>Gets a value indicating whether this component is currently attached to a node.</summary>
    [JsonIgnore, HideInEditor]
    public bool IsAttached => Node is not null; // uses property, safe here since we null-check

    /// <summary>Gets a value indicating whether this component has been awoken.</summary>
    [JsonIgnore, HideInEditor]
    public bool IsAwake { get; private set; }

    /// <summary>Gets a value indicating whether this component has started.</summary>
    [JsonIgnore, HideInEditor]
    public bool IsStarted { get; private set; }

    /// <summary>
    /// Attaches this component to a node and runs Awake if not yet awoken.
    /// </summary>
    public void Setup(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (Node is not null && !ReferenceEquals(Node, node))
            OnDetached();

        Node = node; // backing field via property
        OnAttached();

        if (!IsAwake)
        {
            IsAwake = true;
            OnAwake();
        }

        if (IsActive)
            OnEnable();
    }

    /// <summary>
    /// Detaches this component from its parent node.
    /// </summary>
    public void Detach()
    {
        if (Node is null) return;

        OnDisable();
        OnDetached();
        OnDestroy();
        Node = null;
        IsAwake = false;
        IsStarted = false;
    }

    /// <summary>
    /// Ensures OnStart runs exactly once, deferred to the first frame.
    /// </summary>
    public void EnsureStarted()
    {
        if (IsStarted) return;
        IsStarted = true;
        OnStart();
    }

    // ── Lifecycle callbacks ────────────────────────────────────────────────

    /// <summary>Called once on first attachment, even if inactive. Equivalent to Unity's Awake.</summary>
    public virtual void OnAwake()
    {
    }

    /// <summary>Called when the component becomes active. Also called after Awake if active.</summary>
    public virtual void OnEnable()
    {
    }

    /// <summary>Called once before the first OnUpdate, deferred to first frame.</summary>
    public virtual void OnStart()
    {
    }

    /// <summary>Called every frame while active.</summary>
    public virtual void OnUpdate(float deltaTime)
    {
    }

    /// <summary>Called every frame after all OnUpdate calls.</summary>
    public virtual void OnLateUpdate(float deltaTime)
    {
    }

    /// <summary>Called at a fixed timestep, for physics.</summary>
    public virtual void OnFixedUpdate(float fixedDeltaTime)
    {
    }

    /// <summary>Called when the component becomes inactive.</summary>
    public virtual void OnDisable()
    {
    }

    /// <summary>Called when the component is permanently removed or the node is destroyed.</summary>
    public virtual void OnDestroy()
    {
    }

    /// <summary>Called after the component is attached to a node.</summary>
    public virtual void OnAttached()
    {
    }

    /// <summary>Called before the component is detached from its node.</summary>
    public virtual void OnDetached()
    {
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks if multiple instances of the specified component type are allowed on a single node.
    /// </summary>
    public static bool AllowsMultiple(Type componentType)
        => componentType.GetCustomAttribute<DisallowMultipleComponentAttribute>(inherit: true) is null;

    /// <summary>
    /// Gets the required component types for the specified component type.
    /// </summary>
    public static IEnumerable<Type> GetRequiredComponents(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        return componentType
            .GetCustomAttributes(typeof(RequireComponentAttribute))
            .OfType<RequireComponentAttribute>()
            .Select(a => a.ComponentType);
    }
}
