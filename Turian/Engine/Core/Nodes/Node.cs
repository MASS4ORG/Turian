namespace Turian.Engine.Core;

/// <summary>
/// Represents a node in the engine's scene graph hierarchy.
/// </summary>
[NodeContextMenu]
[TypeId("a3000000-0000-4000-8000-000000000002")]
public partial class Node : IdClass
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
    /// Gets or sets the parent node of this node.
    /// </summary>
    [JsonIgnore, HideInEditor]
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

    /// <summary>
    /// Gets or sets the list of child nodes.
    /// </summary>
    [HideInEditor]
    public ObservableCollection<Node> Children { get; set; } = [];

    /// <summary>
    /// Gets the list of components attached to this node.
    /// </summary>
    [JsonConverter(typeof(ComponentJsonConverter))]
    [HideInEditor]
    public Collection<Component> Components { get; init; } = [];

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
    [JsonIgnore, HideInEditor]
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
        foreach (var component in Components)
            component.Setup(this);
        foreach (var child in Children)
            child.Awake(this);
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
