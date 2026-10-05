namespace Turian.Engine.Core;

static class LayerRegistrationMerge
{
    internal static List<T> Values<T>(List<T> code, List<T> authored) where T : DataAsset
    {
        var overrides = Index(authored);
        var registered = new HashSet<Guid>();
        var merged = new List<T>();
        foreach (var value in code)
        {
            if (!registered.Add(value.Id))
                throw new InvalidOperationException($"Code identity {value.Id} is registered more than once.");
            merged.Add(overrides.Remove(value.Id, out var asset) ? asset : value);
        }
        merged.AddRange(authored.Where(asset => overrides.ContainsKey(asset.Id)));
        return merged;
    }

    internal static List<LayerGroupAsset> Groups(List<LayerGroupAsset> code, List<LayerGroupAsset> authored,
        List<LayerValueAsset> values)
    {
        var groups = Values(code, authored);
        var overrides = Index(values);
        var result = new List<LayerGroupAsset>();
        foreach (var group in groups)
        {
            if (overrides.Count == 0)
            {
                result.Add(group);
                continue;
            }
            var enriched = group.Values.Select(value => overrides.Remove(value.Id, out var asset) ? asset : value).ToList();
            result.Add(new LayerGroupAsset
            {
                Id = group.Id,
                Name = group.Name,
                Values = enriched,
                DefaultValue = enriched.FirstOrDefault(value => value.Id == group.DefaultValue?.Id),
            });
        }
        if (overrides.Count != 0)
            throw new InvalidOperationException("An included value has no registered group membership.");
        return result;
    }

    static Dictionary<Guid, T> Index<T>(List<T> values) where T : DataAsset
    {
        var indexed = new Dictionary<Guid, T>();
        foreach (var value in values)
        {
            if (!indexed.TryAdd(value.Id, value))
                throw new InvalidOperationException($"Authored identity {value.Id} is registered more than once.");
        }
        return indexed;
    }
}
