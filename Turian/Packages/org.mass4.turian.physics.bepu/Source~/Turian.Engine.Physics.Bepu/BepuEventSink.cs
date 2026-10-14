using System.Runtime.InteropServices;

namespace Turian.Engine.Physics;

/// <summary>
/// Collects contacts and trigger overlaps per worker during a step, then merges them and derives enter, stay and
/// exit by comparing the pairs with the previous step's.
/// </summary>
sealed class BepuEventSink
{
    // Unity reports a collision once surfaces are within its default contact offset.
    const float ContactOffset = 0.01f;

    readonly BepuPhysicsWorld world;
    readonly List<Raw>[] workers;
    readonly List<ContactEvent> contacts = [];
    readonly List<TriggerEvent> triggers = [];
    Dictionary<PairKey, ContactEvent> contactPairs = [];
    Dictionary<PairKey, ContactEvent> nextContactPairs = [];
    Dictionary<PairKey, TriggerEvent> triggerPairs = [];
    Dictionary<PairKey, TriggerEvent> nextTriggerPairs = [];

    public BepuEventSink(BepuPhysicsWorld world, int workerCount)
    {
        this.world = world;
        workers = new List<Raw>[Math.Max(1, workerCount)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = [];
        }
    }

    /// <summary>The contacts of the last step.</summary>
    public ReadOnlySpan<ContactEvent> Contacts => CollectionsMarshal.AsSpan(contacts);

    /// <summary>The trigger overlaps of the last step.</summary>
    public ReadOnlySpan<TriggerEvent> Triggers => CollectionsMarshal.AsSpan(triggers);

    /// <summary>Clears the per-worker buffers before a step.</summary>
    public void BeginStep()
    {
        foreach (var buffer in workers)
        {
            buffer.Clear();
        }
    }

    /// <summary>Records a manifold found by a worker; untouched speculative contacts are dropped.</summary>
    public void Record<TManifold>(int worker, BepuBody a, int colliderA, BepuBody b, int colliderB,
        ref TManifold manifold, bool trigger) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var count = manifold.Count;
        if (count == 0)
        {
            return;
        }

        var deepest = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            deepest = MathF.Max(deepest, manifold.GetDepth(i));
        }

        if (deepest < (trigger ? 0f : -ContactOffset))
        {
            return;
        }

        var contact = new ContactEvent
        {
            BodyA = a.Handle,
            BodyB = b.Handle,
            ColliderA = a.Colliders[colliderA].UserData,
            ColliderB = b.Colliders[colliderB].UserData,
        };
        if (!trigger)
        {
            Describe(ref contact, a, b, ref manifold);
        }

        workers[worker].Add(new Raw(new PairKey(a.Handle.Id, colliderA, b.Handle.Id, colliderB), trigger, contact));
    }

    /// <summary>Merges the workers' records and derives the event states.</summary>
    public void EndStep()
    {
        nextContactPairs.Clear();
        nextTriggerPairs.Clear();
        foreach (var buffer in workers)
        {
            foreach (var raw in buffer)
            {
                Merge(raw);
            }
        }

        contacts.Clear();
        triggers.Clear();
        foreach (var (key, contact) in nextContactPairs)
        {
            contacts.Add(contact with { State = contactPairs.ContainsKey(key) ? PhysicsEventState.Stay : PhysicsEventState.Enter });
        }

        foreach (var (key, trigger) in nextTriggerPairs)
        {
            triggers.Add(trigger with { State = triggerPairs.ContainsKey(key) ? PhysicsEventState.Stay : PhysicsEventState.Enter });
        }

        CarryOrExit(contactPairs, nextContactPairs, contacts,
            static (contact, state) => contact with { State = state, PointCount = state == PhysicsEventState.Exit ? 0 : contact.PointCount });
        CarryOrExit(triggerPairs, nextTriggerPairs, triggers, static (trigger, state) => trigger with { State = state });
        (contactPairs, nextContactPairs) = (nextContactPairs, contactPairs);
        (triggerPairs, nextTriggerPairs) = (nextTriggerPairs, triggerPairs);
    }

    void Merge(in Raw raw)
    {
        var (key, contact) = Normalize(raw);
        if (raw.Trigger)
        {
            nextTriggerPairs.TryAdd(key, new TriggerEvent(contact.BodyA, contact.BodyB, contact.ColliderA,
                contact.ColliderB, PhysicsEventState.Enter));
        }
        else if (!nextContactPairs.TryGetValue(key, out var existing) || existing.PointCount < contact.PointCount)
        {
            nextContactPairs[key] = contact;
        }
    }

    // Sleeping and static pairs are not tested, so a pair that vanished while both sides rest is still touching.
    void CarryOrExit<T>(Dictionary<PairKey, T> previous, Dictionary<PairKey, T> next, List<T> output,
        Func<T, PhysicsEventState, T> withState)
    {
        foreach (var (key, item) in previous)
        {
            if (next.ContainsKey(key))
            {
                continue;
            }

            if (world.IsDormant(key.BodyA) && world.IsDormant(key.BodyB))
            {
                next[key] = item;
                output.Add(withState(item, PhysicsEventState.Stay));
            }
            else
            {
                output.Add(withState(item, PhysicsEventState.Exit));
            }
        }
    }

    void Describe<TManifold>(ref ContactEvent contact, BepuBody a, BepuBody b, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var origin = world.CollidablePosition(a);
        var count = Math.Min(manifold.Count, 4);
        for (var i = 0; i < count; i++)
        {
            manifold.GetContact(i, out var offset, out var normal, out var depth, out _);
            contact.Points[i] = new ContactPointData(origin + offset, normal, -depth);
        }

        contact.PointCount = count;
        var point = contact.Points[0].Point;
        var relative = world.PointVelocity(a, point) - world.PointVelocity(b, point);
        contact.RelativeVelocity = relative;
        var normalSpeed = Vector3.Dot(relative, contact.Points[0].Normal);
        var inverseMass = world.InverseMass(a) + world.InverseMass(b);
        contact.Impulse = normalSpeed < 0f && inverseMass > 0f
            ? -normalSpeed / inverseMass * contact.Points[0].Normal
            : Vector3.Zero;
    }

    static (PairKey Key, ContactEvent Contact) Normalize(in Raw raw)
    {
        var key = raw.Key;
        if (key.BodyA < key.BodyB || (key.BodyA == key.BodyB && key.ColliderA <= key.ColliderB))
        {
            return (key, raw.Contact);
        }

        var contact = raw.Contact;
        var swapped = contact with
        {
            BodyA = contact.BodyB,
            BodyB = contact.BodyA,
            ColliderA = contact.ColliderB,
            ColliderB = contact.ColliderA,
            RelativeVelocity = -contact.RelativeVelocity,
            Impulse = -contact.Impulse,
        };
        for (var i = 0; i < swapped.PointCount; i++)
        {
            var point = swapped.Points[i];
            swapped.Points[i] = point with { Normal = -point.Normal };
        }

        return (new PairKey(key.BodyB, key.ColliderB, key.BodyA, key.ColliderA), swapped);
    }

    readonly record struct PairKey(int BodyA, int ColliderA, int BodyB, int ColliderB);

    readonly record struct Raw(PairKey Key, bool Trigger, ContactEvent Contact);
}
