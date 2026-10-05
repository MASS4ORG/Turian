namespace Turian.Tests;

static class LayerTestData
{
    internal static LayerGroupAsset Group(string name = "Physics", int valueCount = 1)
    {
        var values = Enumerable.Range(0, valueCount)
            .Select(index => new LayerValueAsset { Name = index == 0 ? "Default" : $"Value{index}" }).ToList();
        return new LayerGroupAsset { Name = name, Values = values, DefaultValue = values.FirstOrDefault() };
    }

    internal static LayerInterningService Registry(NodeLayerSettings? settings)
    {
        var provider = Substitute.For<ILayerSettingsProvider>();
        provider.Settings.Returns(settings);
        return new LayerInterningService(provider);
    }

    internal static NodeLayerSettings TypedSettings() => new LayerRegistrationBuilder()
        .Group<Physics>(group => group.Default().Value<Water>().Value<Actors>())
        .Group<Rendering>(group => group.Default()).Tag<Player>().Build();

    /// <summary>A typed physics group fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000100")]
    public sealed record Physics : ILayerGroupKey;

    /// <summary>A typed rendering group fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000200")]
    public sealed record Rendering : ILayerGroupKey;

    /// <summary>A typed water value in the physics fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000101")]
    public sealed record Water : ILayerValueKey<Physics>;

    /// <summary>A key with a different type name and the same value identity.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000101")]
    public sealed record RenamedWater : ILayerValueKey<Physics>;

    /// <summary>A typed actor value in the physics fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000102")]
    public sealed record Actors : ILayerValueKey<Physics>;

    /// <summary>A typed player tag fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000300")]
    public sealed record Player : ITagKey;

    /// <summary>A typed enemy tag fixture.</summary>
    [TypeId("d5f74ca8-a970-4770-a2c6-893d27000301")]
    public sealed record Enemy : ITagKey;

    /// <summary>A group key whose missing TypeId is diagnosed during registration.</summary>
    public sealed record MissingGroupId : ILayerGroupKey;

    /// <summary>A value key whose missing TypeId is diagnosed during registration.</summary>
    public sealed record MissingValueId : ILayerValueKey<Physics>;

    /// <summary>A tag key whose missing TypeId is diagnosed during registration.</summary>
    public sealed record MissingTagId : ITagKey;
}
