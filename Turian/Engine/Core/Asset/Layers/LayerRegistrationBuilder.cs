namespace Turian.Engine.Core;

/// <summary>Creates code-authored identities from typed keys and combines them with asset manifests.</summary>
public sealed class LayerRegistrationBuilder
{
    readonly List<LayerGroupAsset> groups = [];
    readonly List<TagAsset> tags = [];

    /// <summary>Registers a group identified by its key type's explicit TypeId.</summary>
    public LayerRegistrationBuilder Group<TGroup>(Action<LayerGroupBuilder<TGroup>> configure, string? name = null)
        where TGroup : class, ILayerGroupKey
    {
        ArgumentNullException.ThrowIfNull(configure);
        var group = new LayerGroupAsset { Id = LayerKeyIdentity<TGroup>.Id, Name = name ?? typeof(TGroup).Name };
        configure(new LayerGroupBuilder<TGroup>(group));
        groups.Add(group);
        return this;
    }

    /// <summary>Registers a tag identified by its key type's explicit TypeId and optional presentation metadata.</summary>
    public LayerRegistrationBuilder Tag<TTag>(string? name = null, Color32? color = null, string description = "")
        where TTag : class, ITagKey
    {
        tags.Add(new TagAsset
        {
            Id = LayerKeyIdentity<TTag>.Id,
            Name = name ?? typeof(TTag).Name,
            Color = color ?? new Color32(255, 255, 255),
            Description = description,
        });
        return this;
    }

    /// <summary>Includes an asset-authored group without changing its identity.</summary>
    public LayerRegistrationBuilder Include(LayerGroupAsset group)
    {
        ArgumentNullException.ThrowIfNull(group);
        groups.Add(group);
        return this;
    }

    /// <summary>Includes an asset-authored tag without changing its identity.</summary>
    public LayerRegistrationBuilder Include(TagAsset tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        tags.Add(tag);
        return this;
    }

    /// <summary>Builds and validates settings; invalid registrations never produce a usable layout.</summary>
    public NodeLayerSettings Build()
    {
        var settings = new NodeLayerSettings { Groups = [.. groups], Tags = [.. tags] };
        var errors = settings.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return settings;
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
