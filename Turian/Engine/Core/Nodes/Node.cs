namespace Turian.Engine.Core;

/// <summary>
/// Represents a node in the engine's scene graph hierarchy.
/// </summary>
[NodeContextMenu]
[TypeId("f7cc675d-54f2-510b-8e5d-807281e3012d")]
public partial class Node : IdObject
{
    /// <summary>Initializes a node and subscribes its local transform to cache invalidation.</summary>
    public Node()
    {
        Transform.PropertyChanged += TransformChanged;
    }

    /// <summary>
    /// Event that is triggered when the global transform of the node is invalidated.
    /// </summary>
    public event Action? OnGlobalTransformInvalidated;

    /// <summary>
    /// Gets or sets a value indicating whether the node is active.
    /// </summary>
    public bool IsActive
    {
        get => field;
        set
        {
            if (field == value) return;
            field = value;
            if (value)
            {
                OnEnable();
                foreach (var component in Components)
                    if (component.IsActive)
                        component.OnEnable();
            }
            else
            {
                foreach (var component in Components)
                    if (component.IsActive)
                        component.OnDisable();
                OnDisable();
            }
        }
    } = true;

    /// <summary>
    /// Gets or sets the name of the node.
    /// </summary>
    public string Name { get; set; } = "Node";

    /// <summary>
    /// Whether the node was destroyed with its scene. A reference to it reads as missing: it is saved as null and
    /// never resolved to.
    /// </summary>
    [JsonIgnore, Hide]
    public bool IsDestroyed { get; internal set; }

    /// <summary>
    /// Gets or sets the parent node of this node.
    /// </summary>
    [JsonIgnore, Hide]
    public Node? Parent
    {
        get;
        set
        {
            if (field != null)
            {
                field.OnGlobalTransformInvalidated -= InvalidateGlobalTransformCache;
            }

            field = value;

            if (field != null)
            {
                field.OnGlobalTransformInvalidated += InvalidateGlobalTransformCache;
            }

            InvalidateGlobalTransformCache();
        }
    }

    /// <summary>Gets the services bound to this scene hierarchy.</summary>
    [JsonIgnore, Hide]
    public IServiceProvider? Services { get; private set; }

    /// <summary>Whether an edit-time preview may leave gameplay-only services unbound.</summary>
    [JsonIgnore, Hide]
    internal bool AllowMissingServices { get; private set; }

    /// <summary>
    /// Gets or sets the list of child nodes.
    /// </summary>
    [Hide]
    public ObservableCollection<Node> Children { get; set; } = [];

    /// <summary>
    /// Gets the list of components attached to this node.
    /// </summary>
    [JsonConverter(typeof(ComponentJsonConverter))]
    [Hide]
    public Collection<Component> Components { get; init; } = [];

    /// <summary>
    /// The prefab this node was instantiated from, or null when the node is not the root of a prefab instance.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Hide]
    public PrefabInstance? PrefabInstance { get; set; }

    /// <summary>
    /// Gets or sets the transformation data for this node.
    /// </summary>
    public Transform Transform
    {
        get;
        set
        {
            if (field != value)
            {
                field.PropertyChanged -= TransformChanged;
                field = value;
                field.PropertyChanged += TransformChanged;

                InvalidateGlobalTransformCache();
            }
        }
    } = new();

    void TransformChanged(object? sender, PropertyChangedEventArgs? e)
    {
        InvalidateGlobalTransformCache();
    }

    readonly object lockObject = new();

    Transform? cachedGlobalTransform;

    /// <summary>
    /// Gets the cached global transformation of the node.
    /// </summary>
    [JsonIgnore, Hide]
    public Transform GlobalTransform
    {
        get
        {
            lock (lockObject)
            {
                cachedGlobalTransform ??= CalculateGlobalTransform();
                return cachedGlobalTransform;
            }
        }
        set => Transform = Transform.RemoveParent(value);
    }

    /// <summary>
    /// Recursively runs Awake on this node and all children. Call once after deserialization.
    /// </summary>
    public virtual void Awake(Node? parentNew)
    {
        Parent = parentNew;
        Services = parentNew?.Services ?? Services;
        AllowMissingServices = parentNew?.AllowMissingServices ?? AllowMissingServices;
        SceneServiceInjector.Inject(this, Services, AllowMissingServices);
        foreach (var component in Components)
            component.Setup(this);
        foreach (var child in Children)
            child.Awake(this);
    }

    /// <summary>Wakes a scene hierarchy with an explicit service provider.</summary>
    public void Awake(Node? parentNew, IServiceProvider services, bool allowMissingServices = false)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        AllowMissingServices = allowMissingServices;
        Awake(parentNew);
    }

    /// <summary>
    /// Runs EnsureStarted on all components in the subtree. Call once on first frame.
    /// </summary>
    public virtual void Start()
    {
        foreach (var component in Components)
            if (component.IsActive)
                component.EnsureStarted();
        foreach (var child in Children)
            if (child.IsActive)
                child.Start();
    }

    /// <summary>Called when the node becomes active or is enabled.</summary>
    public virtual void OnEnable()
    {
    }

    /// <summary>Called when the node becomes inactive or is disabled.</summary>
    public virtual void OnDisable()
    {
    }

    /// <summary>Called when the node is being destroyed.</summary>
    public virtual void OnDestroy()
    {
    }

    /// <summary>Called each frame after all Updates, for late logic.</summary>
    public virtual void OnLateUpdate(float deltaTime)
    {
    }

    /// <summary>Called at fixed time intervals for physics.</summary>
    public virtual void OnFixedUpdate(float fixedDeltaTime)
    {
    }

    /// <summary>
    /// Called when the node needs to perform an update.
    /// </summary>
    /// <param name="deltaTime">The time elapsed since the last update.</param>
    public virtual void OnUpdate(float deltaTime)
    {
    }
}
