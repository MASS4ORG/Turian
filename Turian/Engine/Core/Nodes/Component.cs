namespace Turian.Engine.Core;

/// <summary>
/// Base class for all components that can be attached to nodes in the scene graph.
/// </summary>
public abstract class Component : IdObject
{
    CancellationTokenSource? destruction;
    volatile bool destructionStarted;

    /// <summary>Cancels before destruction callbacks, allowing gameplay waits to unwind on the game loop.</summary>
    [JsonIgnore, Hide]
    public CancellationToken DestroyCancellationToken
    {
        get
        {
            var source = destruction;
            if (source is null)
            {
                var created = new CancellationTokenSource();
                source = Interlocked.CompareExchange(ref destruction, created, null) ?? created;
                if (!ReferenceEquals(source, created))
                {
                    created.Dispose();
                }
            }
            if (destructionStarted && !source.IsCancellationRequested)
            {
                source.Cancel();
            }

            return source.Token;
        }
    }

    /// <summary>Provides gameplay waits bound to this component and its running game session.</summary>
    [JsonIgnore, Hide]
    public ComponentAsync Async => new(this,
        Services?.GetService<GameLoopScheduler>() ??
        throw new InvalidOperationException("Gameplay async requires a running game session."));

    /// <summary>Gets or sets a value indicating whether this component is active.</summary>
    [Hide]
    public bool IsActive
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            if (IsAttached)
            {
                if (value)
                {
                    OnEnable();
                }
                else
                {
                    OnDisable();
                }
            }
        }
    } = true;

    /// <summary>
    /// Whether the component was destroyed (detached from its node). A reference to it reads as missing: it is
    /// saved as null and never resolved to.
    /// </summary>
    [JsonIgnore, Hide]
    public bool IsDestroyed { get; private set; }

    /// <summary>Gets the node this component is attached to.</summary>
    [JsonIgnore, Hide]
    public Node? Node { get; private set; }

    /// <summary>Gets the service provider associated with this component's scene.</summary>
    [JsonIgnore, Hide]
    public IServiceProvider? Services => Node?.Services;

    /// <summary>Gets a value indicating whether this component is currently attached to a node.</summary>
    [JsonIgnore, Hide]
    public bool IsAttached => Node is not null; // uses property, safe here since we null-check

    /// <summary>Gets a value indicating whether this component has been awoken.</summary>
    [JsonIgnore, Hide]
    public bool IsAwake { get; private set; }

    /// <summary>Gets a value indicating whether this component has started.</summary>
    [JsonIgnore, Hide]
    public bool IsStarted { get; private set; }

    /// <summary>Whether <see cref="Destroy"/> or <see cref="DestroyImmediate"/> was requested for this component.</summary>
    [JsonIgnore, Hide]
    public bool DestroyQueued { get; private set; }

    /// <summary>The node components collection listing this component, which may precede attachment.</summary>
    internal ComponentCollection? ListedIn { get; set; }

    /// <summary>
    /// Removes and destroys this component at the next boundary of the running game loop, as <see cref="Node.Destroy"/>
    /// does: the end of the fixed tick when requested from a fixed update, otherwise the end of the frame. Until then
    /// it stays attached and keeps updating.
    /// </summary>
    public void Destroy()
    {
        if (IsDestroyed || DestroyQueued)
        {
            return;
        }

        if (Node is not { } node)
        {
            DestroyImmediate();
            return;
        }

        DestroyQueued = true;
        node.QueueDestroy(this);
    }

    /// <summary>Removes and destroys this component now, for editor and tool code outside the game loop.</summary>
    public void DestroyImmediate()
    {
        if (IsDestroyed)
        {
            return;
        }

        DestroyQueued = true;
        ListedIn?.Remove(this);
        if (Node is null)
        {
            IsDestroyed = true;
        }

        Detach();
        if (this is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Attaches this component to a node and runs Awake if not yet awoken.
    /// </summary>
    public void Setup(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (Node is not null && !ReferenceEquals(Node, node))
        {
            OnDetached();
        }

        Node = node; // backing field via property
        SceneServiceInjector.Inject(this, node.Services, node.AllowMissingServices);
        OnAttached();

        if (!IsAwake)
        {
            IsAwake = true;
            OnAwake();
        }

        if (IsActive)
        {
            OnEnable();
        }
    }

    /// <summary>
    /// Detaches this component from its parent node.
    /// </summary>
    public void Detach()
    {
        if (Node is null)
        {
            return;
        }

        destructionStarted = true;
        try { destruction?.Cancel(); }
        catch (Exception ex) { GameLoopScheduler.Report(ex, this); }
        OnDisable();
        OnDetached();
        OnDestroy();
        IsDestroyed = true;
        Node = null;
        IsAwake = false;
        IsStarted = false;
    }

    /// <summary>
    /// Ensures OnStart runs exactly once, deferred to the first frame.
    /// </summary>
    public void EnsureStarted()
    {
        if (IsStarted)
        {
            return;
        }

        IsStarted = true;
        OnStart();
    }

    // ── Lifecycle callbacks ────────────────────────────────────────────────

    /// <summary>Called once on first attachment, even if inactive. Runs before the first update.</summary>
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

    /// <summary>The per-frame callbacks a component type overrides; the ticker skips the empty base methods.</summary>
    [Flags]
    internal enum FrameCallbacks : byte
    {
        None = 0,
        Update = 1,
        LateUpdate = 2,
        FixedUpdate = 4,
        Known = 8,
    }

    // Weak keys let collectible user-code types unload.
    static readonly ConditionalWeakTable<Type, StrongBox<FrameCallbacks>> OverriddenCallbacks = [];
    FrameCallbacks frameCallbacks;

    /// <summary>Whether this component's type overrides <paramref name="callback"/>, resolved once per type.</summary>
    internal bool Overrides(FrameCallbacks callback)
    {
        if (frameCallbacks == FrameCallbacks.None)
        {
            frameCallbacks = OverriddenCallbacks.GetValue(GetType(), static type => new(FindOverrides(type))).Value;
        }

        return (frameCallbacks & callback) != 0;
    }

    static FrameCallbacks FindOverrides(Type type)
    {
        var callbacks = FrameCallbacks.Known;
        if (Overridden(type, nameof(OnUpdate)))
        {
            callbacks |= FrameCallbacks.Update;
        }

        if (Overridden(type, nameof(OnLateUpdate)))
        {
            callbacks |= FrameCallbacks.LateUpdate;
        }

        if (Overridden(type, nameof(OnFixedUpdate)))
        {
            callbacks |= FrameCallbacks.FixedUpdate;
        }

        return callbacks;
    }

    // A method hiding the callback with "new" also counts, which only costs an empty call.
    static bool Overridden(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [typeof(float)])?.DeclaringType != typeof(Component);

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
            .Select(static a => a.ComponentType);
    }
}
