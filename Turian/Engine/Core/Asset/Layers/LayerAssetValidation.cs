namespace Turian.Engine.Core;

static class LayerAssetValidation
{
    internal static IReadOnlyList<string> Validate(NodeLayerSettings master)
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

        ValidateCapacity(tags.Count, ushort.MaxValue + 1, "Tags", errors);
        foreach (var group in groups)
        {
            if (group is null)
            {
                errors.Add("Group reference is unresolved.");
                continue;
            }

            ValidateName(group.Name, "Group", errors);
            ValidateIdentity(group, $"Group '{group.Name}'", identities, errors);
            ValidateGroup(group, identities, errors);
        }

        ValidateTags(tags, identities, errors);
        ValidateConsumerGroup(master.PhysicsGroup, groups, "Physics", errors);
        ValidateConsumerGroup(master.RenderingGroup, groups, "Rendering", errors);
        return errors;
    }

    static void ValidateConsumerGroup(LayerGroupAsset? group, List<LayerGroupAsset> groups, string consumer,
        List<string> errors)
    {
        if (group is not null && !groups.Any(registered => registered?.Id == group.Id))
            errors.Add($"{consumer} group must reference a group in the settings manifest.");
    }

    static void ValidateGroup(LayerGroupAsset group, Dictionary<Guid, string> identities, List<string> errors)
    {
        if (group.Values is null)
        {
            errors.Add($"Group '{group.Name}' value manifest cannot be null.");
            return;
        }

        ValidateCapacity(group.Values.Count, byte.MaxValue + 1, $"Group '{group.Name}' values", errors);
        foreach (var value in group.Values)
        {
            if (value is null)
            {
                errors.Add($"Group '{group.Name}' has an unresolved value reference.");
                continue;
            }

            ValidateName(value.Name, $"Group '{group.Name}' value", errors);
            ValidateIdentity(value, $"Group '{group.Name}' value '{value.Name}'", identities, errors);
        }

        if (group.DefaultValue is null || !group.Values.Any(value => value?.Id == group.DefaultValue.Id))
            errors.Add($"Group '{group.Name}' default must reference a value in its manifest.");
    }

    static void ValidateTags(List<TagAsset> tags, Dictionary<Guid, string> identities, List<string> errors)
    {
        foreach (var tag in tags)
        {
            if (tag is null)
            {
                errors.Add("Tag reference is unresolved.");
                continue;
            }

            ValidateName(tag.Name, "Tag", errors);
            ValidateIdentity(tag, $"Tag '{tag.Name}'", identities, errors);
        }
    }

    static void ValidateCapacity(int count, int capacity, string source, List<string> errors)
    {
        if (count > capacity) errors.Add($"{source} count {count} exceeds capacity {capacity}.");
    }

    static void ValidateName(string name, string source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(name)) errors.Add($"{source} name cannot be empty.");
    }

    static void ValidateIdentity(IdObject asset, string source, Dictionary<Guid, string> identities,
        List<string> errors)
    {
        if (asset.Id == Guid.Empty) errors.Add($"{source} identity cannot be empty.");
        else if (!identities.TryAdd(asset.Id, source))
            errors.Add($"{source} identity {asset.Id} is already used by {identities[asset.Id]}.");
    }
}
