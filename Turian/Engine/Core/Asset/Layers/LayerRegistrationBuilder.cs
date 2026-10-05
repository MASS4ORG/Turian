namespace Turian.Engine.Core;

/// <summary>Creates code-authored identities and combines them with asset-authored manifests.</summary>
public sealed class LayerRegistrationBuilder
{
    static readonly Guid IdentityNamespace = new("2974087e-1a25-4efc-b5c5-17b5f0d29972");
    readonly string module;
    readonly List<LayerGroupAsset> groups = [];
    readonly List<TagAsset> tags = [];

    /// <summary>Uses a stable module key as the identity namespace for code registrations.</summary>
    public LayerRegistrationBuilder(string module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        this.module = module;
    }

    /// <summary>Registers an independent group whose first declared value is its explicit default.</summary>
    public LayerRegistrationBuilder Group(string key, Action<LayerGroupBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(configure);
        var group = new LayerGroupAsset { Id = Identity("group", key), Name = key };
        configure(new LayerGroupBuilder(group));
        groups.Add(group);
        return this;
    }

    /// <summary>Registers a tag with a stable identity derived from its module and key.</summary>
    public LayerRegistrationBuilder Tag(string key, string? name = null, Color32? color = null,
        string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        tags.Add(new TagAsset
        {
            Id = Identity("tag", key),
            Name = name ?? key,
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

    /// <summary>Builds and validates a master; invalid registrations never produce a usable layout.</summary>
    public MasterNodeLayersAsset Build()
    {
        var master = new MasterNodeLayersAsset
        {
            Id = Identity("master", string.Empty),
            Groups = [.. groups],
            Tags = [.. tags],
        };
        var errors = master.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return master;
    }

    Guid Identity(string kind, string key) => AssetIdFactory.Derive(IdentityNamespace,
        FormattableString.Invariant($"v1/{module.Length}:{module}/{kind}/{key.Length}:{key}"));
}

/// <summary>Declares code-authored values in one independent layer group.</summary>
public sealed class LayerGroupBuilder
{
    readonly LayerGroupAsset group;

    internal LayerGroupBuilder(LayerGroupAsset group) => this.group = group;

    /// <summary>Adds a value with a stable key and optional presentation metadata.</summary>
    public LayerGroupBuilder Value(string key, string? name = null, Color32? color = null, string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = new LayerValueAsset
        {
            Id = AssetIdFactory.Derive(group.Id, FormattableString.Invariant($"v1/value/{key.Length}:{key}")),
            Name = name ?? key,
            Color = color ?? new Color32(255, 255, 255),
            Description = description,
        };
        group.Values.Add(value);
        group.DefaultValue ??= value;
        return this;
    }
}
