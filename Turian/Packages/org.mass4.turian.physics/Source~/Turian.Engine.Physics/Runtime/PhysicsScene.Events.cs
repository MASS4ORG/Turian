namespace Turian.Engine.Physics;

public sealed partial class PhysicsScene
{
    // Enough for debug drawing a busy scene; more would only slow the editor.
    const int MaxRecordedContacts = 4096;

    readonly List<ContactPoint> lastContacts = [];

    /// <summary>The touching contact points of the last step, seen from the first collider of each pair.</summary>
    public IReadOnlyList<ContactPoint> LastContacts => lastContacts;

    // Callbacks run after the step on the game loop thread; they may change the scene, so events are copied first.
    void DispatchEvents()
    {
        var world = World!;
        var contacts = world.Contacts.ToArray();
        var triggers = world.Triggers.ToArray();
        lastContacts.Clear();
        foreach (var contact in contacts)
        {
            if (Resolve(contact.ColliderA, contact.ColliderB) is var (a, b))
            {
                Record(a, b, contact);
                SendCollision(a, b, contact, 1f);
                SendCollision(b, a, contact, -1f);
            }
        }

        foreach (var trigger in triggers)
        {
            if (Resolve(trigger.ColliderA, trigger.ColliderB) is var (a, b))
            {
                SendTrigger(a, b, trigger.State);
                SendTrigger(b, a, trigger.State);
            }
        }
    }

    // Pairs whose collider was disabled or destroyed are dropped, as Unity drops their exits.
    (ColliderComponent, ColliderComponent)? Resolve(ulong a, ulong b) =>
        collidersById.TryGetValue(a, out var first) && collidersById.TryGetValue(b, out var second)
            ? (first, second)
            : null;

    void Record(ColliderComponent a, ColliderComponent b, in ContactEvent contact)
    {
        for (var i = 0; i < contact.PointCount && lastContacts.Count < MaxRecordedContacts; i++)
        {
            var point = contact.Points[i];
            lastContacts.Add(new ContactPoint(point.Point, point.Normal, point.Separation, a, b));
        }
    }

    void SendCollision(ColliderComponent self, ColliderComponent other, in ContactEvent contact, float sign)
    {
        var points = new ContactPoint[contact.PointCount];
        for (var i = 0; i < points.Length; i++)
        {
            var point = contact.Points[i];
            points[i] = new ContactPoint(point.Point, point.Normal * sign, point.Separation, self, other);
        }

        var collision = new Collision(self, other, points, contact.Impulse * sign, contact.RelativeVelocity * sign);
        var state = contact.State;
        foreach (var handler in Handlers<ICollisionHandler>(self))
        {
            Invoke(handler, state switch
            {
                PhysicsEventState.Enter => static (h, c) => h.OnCollisionEnter(c),
                PhysicsEventState.Stay => static (h, c) => h.OnCollisionStay(c),
                _ => static (h, c) => h.OnCollisionExit(c),
            }, collision);
        }
    }

    void SendTrigger(ColliderComponent self, ColliderComponent other, PhysicsEventState state)
    {
        foreach (var handler in Handlers<ITriggerHandler>(self))
        {
            Invoke(handler, state switch
            {
                PhysicsEventState.Enter => static (h, o) => h.OnTriggerEnter(o),
                PhysicsEventState.Stay => static (h, o) => h.OnTriggerStay(o),
                _ => static (h, o) => h.OnTriggerExit(o),
            }, other);
        }
    }

    // The collider's node and, when different, its rigidbody's node receive the callbacks, as in Unity.
    static List<T> Handlers<T>(ColliderComponent collider) where T : class
    {
        var handlers = new List<T>();
        Collect(collider.Node, handlers);
        if (collider.AttachedRigidbody?.Node is { } bodyNode && bodyNode != collider.Node)
        {
            Collect(bodyNode, handlers);
        }

        return handlers;
    }

    static void Collect<T>(Node? node, List<T> handlers) where T : class
    {
        if (node is not { IsActiveInHierarchy: true })
        {
            return;
        }

        foreach (var component in node.Components)
        {
            if (component is T handler && component.IsActive)
            {
                handlers.Add(handler);
            }
        }
    }

    static void Invoke<THandler, TArgument>(THandler handler, Action<THandler, TArgument> callback, TArgument argument)
    {
        try
        {
            callback(handler, argument);
        }
        catch (Exception ex)
        {
            Log.Logger.LogError(ex, "{Handler} threw in a physics callback", handler?.GetType().Name);
        }
    }
}
