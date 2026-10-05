using static Turian.Tests.LayerTestData;

namespace Turian.Tests;

/// <summary>Checks designer asset enrichment of typed identities and conflicting authoring sources.</summary>
public sealed class LayerEnrichmentTests
{
    /// <summary>A designer asset supplies presentation for code-defined values and tags without changing identity.</summary>
    [Fact]
    public void AssetsEnrichTypedIdentities()
    {
        var declared = TypedSettings();
        var water = new LayerValueAsset
        {
            Id = declared.Groups[0].Values[1].Id,
            Name = "Sea",
            Color = new Color32(1, 2, 3),
            Description = "Designer data",
        };
        var tag = new TagAsset { Id = declared.Tags[0].Id, Name = "Hero" };
        var settings = new LayerRegistrationBuilder().Include(water).Include(tag)
            .Group<Physics>(group => group.Default().Value<Water>()).Tag<Player>().Build();
        Assert.Same(water, settings.Groups[0].Values[1]);
        Assert.Same(tag, settings.Tags[0]);
        Assert.True(Registry(settings).TryGetLayerIndex<Physics, Water>(out var index));
        Assert.Equal(1, index);
        Assert.True(Registry(settings).TryGetTagId<Player>(out _));
    }

    /// <summary>A matching group asset supplies the complete designer manifest and explicit fallback.</summary>
    [Fact]
    public void GroupAssetsSupplyManifestAndDefault()
    {
        var original = TypedSettings();
        var group = original.Groups[0];
        group.Values.Reverse();
        group.DefaultValue = group.Values[0];
        var settings = new LayerRegistrationBuilder()
            .Group<Physics>(builder => builder.Default().Value<Water>()).Include(group).Build();
        Assert.Same(group, settings.Groups[0]);
        Assert.Equal(group.DefaultValue.Id, Registry(settings).GetLayerId(0, 0));
        var defaultAsset = new LayerValueAsset { Id = group.DefaultValue.Id, Name = "Designer default" };
        var enriched = new LayerRegistrationBuilder().Include(group).Include(defaultAsset).Build();
        Assert.Same(defaultAsset, enriched.Groups[0].DefaultValue);
        Assert.NotSame(group, enriched.Groups[0]);
        Assert.NotSame(defaultAsset, group.DefaultValue);
    }

    /// <summary>Standalone value enrichment cannot register unowned identities or repeat authored identities.</summary>
    [Fact]
    public void InvalidEnrichmentsAreRejected()
    {
        var value = new LayerValueAsset { Name = "Unowned" };
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder().Include(value).Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Include(value).Include(value).Build());
        var settings = new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default().Value<Water>())
            .Group<Rendering>(group => group.Default())
            .Include(new LayerValueAsset { Id = TypedSettings().Groups[0].Values[1].Id, Name = "Sea" }).Build();
        Assert.Equal(2, settings.Groups.Count);
        Assert.Empty(settings.Validate());
    }
}
