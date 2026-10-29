namespace Turian.Engine.Core;

public static partial class ObjectReferences
{
    static readonly ConditionalWeakTable<IdObject, Dictionary<string, Func<IdObject?, bool>>> Embedded = new();

    /// <summary>Reads an inline value's GUID wire with a compiled setter; missing targets keep their serialized identity.</summary>
    public static void ReadEmbedded<TData>(IdObject owner, string member, JsonElement json, Action<TData?> assign)
    {
        ArgumentNullException.ThrowIfNull(assign);
        Forget(owner, member);
        assign(default);
        if (json.ValueKind == JsonValueKind.Null) { return; }
        if (!TryRead(owner, member, typeof(TData), json, allowSceneObjects: false, out _))
        {
            throw new JsonException("An embedded asset wire requires a GUID reference.");
        }
        if (!TryGetUnresolved(owner, member, out _)) { return; }
        Embedded.GetOrCreateValue(owner)[member] = value =>
        {
            if (value is not TData typed) { return false; }
            assign(typed);
            return true;
        };
    }

    static void ForgetEmbedded(IdObject owner, string member)
    {
        if (!Embedded.TryGetValue(owner, out var setters)) { return; }
        setters.Remove(member);
        if (setters.Count == 0) { Embedded.Remove(owner); }
    }

    /// <summary>Writes an inline value's asset identity, preserving a temporarily unresolved target.</summary>
    public static void WriteEmbedded(Utf8JsonWriter writer, IdObject owner, string member, DataAsset? value)
    {
        TryGetUnresolved(owner, member, out var pending);
        WriteRef(writer, value?.Id ?? pending.FirstOrDefault());
    }

    static bool TryAssignEmbedded(IdObject owner, string member, Guid[] ids, Func<Guid, IdObject?> find,
        out int missing)
    {
        missing = 0;
        if (!Embedded.TryGetValue(owner, out var setters) || !setters.TryGetValue(member, out var assign))
        {
            return false;
        }
        if (ids.Length == 1 && assign(find(ids[0])))
        {
            setters.Remove(member);
            if (setters.Count == 0) { Embedded.Remove(owner); }
            return true;
        }
        var pending = PendingTable(owner);
        lock (pending) { pending.Add(new(member, ids)); }
        missing = ids.Length;
        return true;
    }
}
