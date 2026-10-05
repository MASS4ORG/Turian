namespace Turian.Engine.Core;

static class LayerAssetValidation
{
    internal static IReadOnlyList<string> Validate(MasterNodeLayersAsset master)
    {
        var errors = new List<string>();
        var identities = new Dictionary<Guid, string>();
        ValidateIdentity(master, "Master", identities, errors);
        var groups = master.Groups;
        var tags = master.Tags;
        if (groups is null || tags is null)
        {
            errors.Add("Master group and tag manifests cannot be null.");
            return errors;
        }

        ValidateCapacity(groups.Count, NodeLayers.Capacity, "Groups", errors);
        ValidateCapacity(tags.Count, ushort.MaxValue + 1, "Tags", errors);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null)
            {
                errors.Add("Group reference is unresolved.");
                continue;
            }

            ValidateName(group.Name, "Group", names, errors);
            ValidateIdentity(group, $"Group '{group.Name}'", identities, errors);
            ValidateGroup(group, identities, errors);
        }

        ValidateTags(tags, identities, errors);
        return errors;
    }

    static void ValidateGroup(LayerGroupAsset group, Dictionary<Guid, string> identities, List<string> errors)
    {
        if (group.Values is null)
        {
            errors.Add($"Group '{group.Name}' value manifest cannot be null.");
            return;
        }

        ValidateCapacity(group.Values.Count, byte.MaxValue + 1, $"Group '{group.Name}' values", errors);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in group.Values)
        {
            if (value is null)
            {
                errors.Add($"Group '{group.Name}' has an unresolved value reference.");
                continue;
            }

            ValidateName(value.Name, $"Group '{group.Name}' value", names, errors);
            ValidateIdentity(value, $"Group '{group.Name}' value '{value.Name}'", identities, errors);
        }

        if (group.DefaultValue is null || !group.Values.Any(value => value?.Id == group.DefaultValue.Id))
            errors.Add($"Group '{group.Name}' default must reference a value in its manifest.");
    }

    static void ValidateTags(List<TagAsset> tags, Dictionary<Guid, string> identities, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (tag is null)
            {
                errors.Add("Tag reference is unresolved.");
                continue;
            }

            ValidateName(tag.Name, "Tag", names, errors);
            ValidateIdentity(tag, $"Tag '{tag.Name}'", identities, errors);
        }
    }

    static void ValidateCapacity(int count, int capacity, string source, List<string> errors)
    {
        if (count > capacity) errors.Add($"{source} count {count} exceeds capacity {capacity}.");
    }

    static void ValidateName(string name, string source, HashSet<string> names, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name)) errors.Add($"{source} name cannot be empty.");
        else if (!names.Add(name)) errors.Add($"{source} name '{name}' is duplicated.");
    }

    static void ValidateIdentity(IdObject asset, string source, Dictionary<Guid, string> identities,
        List<string> errors)
    {
        if (asset.Id == Guid.Empty) errors.Add($"{source} identity cannot be empty.");
        else if (!identities.TryAdd(asset.Id, source))
            errors.Add($"{source} identity {asset.Id} is already used by {identities[asset.Id]}.");
    }
}
