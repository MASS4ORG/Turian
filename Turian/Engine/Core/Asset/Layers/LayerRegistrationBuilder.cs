namespace Turian.Engine.Core;

/// <summary>Creates code-authored identities from typed keys and combines them with asset manifests.</summary>
public sealed class LayerRegistrationBuilder
{
    readonly List<LayerGroupAsset> groups = [];
    readonly List<TagAsset> tags = [];
    readonly List<LayerGroupAsset> assetGroups = [];
    readonly List<TagAsset> assetTags = [];
    readonly List<LayerValueAsset> assetValues = [];
    readonly List<Guid> groupOrder = [];
    readonly List<Guid> tagOrder = [];

    /// <summary>Registers a group identified by its key type's explicit TypeId.</summary>
    public LayerRegistrationBuilder Group<TGroup>(Action<LayerGroupBuilder<TGroup>> configure, string? name = null)
        where TGroup : class, ILayerGroupKey
    {
        ArgumentNullException.ThrowIfNull(configure);
        var group = new LayerGroupAsset { Id = LayerKeyIdentity<TGroup>.Id, Name = name ?? typeof(TGroup).Name };
        configure(new LayerGroupBuilder<TGroup>(group));
        groups.Add(group);
        groupOrder.Add(group.Id);
        return this;
    }

    /// <summary>Registers a tag identified by its key type's explicit TypeId and optional presentation metadata.</summary>
    public LayerRegistrationBuilder Tag<TTag>(string? name = null, Color32? color = null, string description = "")
        where TTag : class, ITagKey
    {
        var tag = new TagAsset
        {
            Id = LayerKeyIdentity<TTag>.Id,
            Name = name ?? typeof(TTag).Name,
            Color = color ?? new Color32(255, 255, 255),
            Description = description,
        };
        tags.Add(tag);
        tagOrder.Add(tag.Id);
        return this;
    }

    /// <summary>Includes an authored group, enriching a matching code identity with the designer's manifest.</summary>
    public LayerRegistrationBuilder Include(LayerGroupAsset group)
    {
        ArgumentNullException.ThrowIfNull(group);
        assetGroups.Add(group);
        groupOrder.Add(group.Id);
        return this;
    }

    /// <summary>Includes an authored tag, enriching a matching code identity with presentation data.</summary>
    public LayerRegistrationBuilder Include(TagAsset tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        assetTags.Add(tag);
        tagOrder.Add(tag.Id);
        return this;
    }

    /// <summary>Enriches a registered value whose identity matches this authored asset.</summary>
    public LayerRegistrationBuilder Include(LayerValueAsset value)
    {
        ArgumentNullException.ThrowIfNull(value);
        assetValues.Add(value);
        return this;
    }

    /// <summary>Builds and validates settings; invalid registrations never produce a usable layout.</summary>
    public NodeLayerSettings Build()
    {
        var settings = new NodeLayerSettings
        {
            Groups = Order(LayerRegistrationMerge.Groups(groups, assetGroups, assetValues), groupOrder),
            Tags = Order(LayerRegistrationMerge.Values(tags, assetTags), tagOrder),
        };
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return settings;
    }

    static List<T> Order<T>(List<T> values, List<Guid> order) where T : DataAsset
    {
        var indexed = values.ToDictionary(value => value.Id);
        return [.. order.Distinct().Select(id => indexed[id])];
    }
}

/// <summary>Declares values whose key types belong to one specific group.</summary>
public sealed class LayerGroupBuilder<TGroup> where TGroup : class, ILayerGroupKey
{
    readonly LayerGroupAsset group;

    internal LayerGroupBuilder(LayerGroupAsset group) => this.group = group;

    /// <summary>Declares the group's default with an identity derived from the group's TypeId.</summary>
    public LayerGroupBuilder<TGroup> Default(string name = "Default")
    {
        if (group.DefaultValue is not null) throw new InvalidOperationException("A group can declare one default.");
        var value = new LayerValueAsset { Id = AssetIdFactory.Derive(group.Id, "default"), Name = name };
        group.Values.Add(value);
        group.DefaultValue = value;
        return this;
    }

    /// <summary>Declares a typed default value with optional presentation metadata.</summary>
    public LayerGroupBuilder<TGroup> Default<TValue>(string? name = null, Color32? color = null,
        string description = "") where TValue : class, ILayerValueKey<TGroup>
    {
        if (group.DefaultValue is not null) throw new InvalidOperationException("A group can declare one default.");
        Value<TValue>(name, color, description);
        group.DefaultValue = group.Values[^1];
        return this;
    }

    /// <summary>Adds a value identified by its key type's explicit TypeId and optional presentation metadata.</summary>
    public LayerGroupBuilder<TGroup> Value<TValue>(string? name = null, Color32? color = null,
        string description = "") where TValue : class, ILayerValueKey<TGroup>
    {
        group.Values.Add(new LayerValueAsset
        {
            Id = LayerKeyIdentity<TValue>.Id,
            Name = name ?? typeof(TValue).Name,
            Color = color ?? new Color32(255, 255, 255),
            Description = description,
        });
        return this;
    }
}
