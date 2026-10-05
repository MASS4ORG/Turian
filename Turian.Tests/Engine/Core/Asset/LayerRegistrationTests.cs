namespace Turian.Tests;

/// <summary>Checks code-authored identity compatibility, registration conflicts, and asset composition.</summary>
public sealed class LayerRegistrationTests
{
    /// <summary>Golden identities protect the versioned UTF-8 identity derivation contract.</summary>
    [Fact]
    public void CodeIdentitiesAreStable()
    {
        var first = BuildSample();
        var second = BuildSample();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Guid.Parse("9cf074e6-d693-125a-bb22-4f6bfd8776f3"), first.Groups[0].Id);
        Assert.Equal(Guid.Parse("b5d564f5-3c82-a453-8a6b-c3ebccb16a62"), first.Groups[0].Values[1].Id);
        Assert.Equal(Guid.Parse("3e611c3c-60b6-5a51-b06e-10b453f75e91"), first.Tags[0].Id);
        Assert.Equal(first.Groups[0].Values[1].Id, second.Groups[0].Values[1].Id);
        var other = new LayerRegistrationBuilder("Other")
            .Group("Physics", group => group.Value("Default").Value("Water")).Build();
        Assert.NotEqual(first.Groups[0].Id, other.Groups[0].Id);
    }

    /// <summary>Changing presentation metadata retains code identity while keeping the explicit default.</summary>
    [Fact]
    public void PresentationIsIndependentOfIdentity()
    {
        var master = new LayerRegistrationBuilder("Sample")
            .Group("Physics", group => group.Value("Default")
                .Value("Water", "Sea", new Color32(10, 20, 30), "Water surfaces"))
            .Tag("Player", "Hero", new Color32(40, 50, 60), "Playable actors").Build();
        var value = master.Groups[0].Values[1];
        Assert.Equal(BuildSample().Groups[0].Values[1].Id, value.Id);
        Assert.Equal("Sea", value.Name);
        Assert.Equal(new Color32(10, 20, 30), value.Color);
        Assert.Equal("Water surfaces", value.Description);
        Assert.Same(master.Groups[0].Values[0], master.Groups[0].DefaultValue);
        Assert.Equal(BuildSample().Tags[0].Id, master.Tags[0].Id);
        Assert.Equal("Hero", master.Tags[0].Name);
        Assert.Equal(new Color32(40, 50, 60), master.Tags[0].Color);
        Assert.Equal("Playable actors", master.Tags[0].Description);
    }

    /// <summary>Asset groups and tags use the same manifest and retain their authored identities.</summary>
    [Fact]
    public void AssetAndCodeRegistrationsCompose()
    {
        var assets = BuildSample();
        var builder = new LayerRegistrationBuilder("Gameplay")
            .Include(assets.Groups[0]).Include(assets.Tags[0])
            .Group("Gameplay", group => group.Value("Default").Value("Friendly")).Tag("Enemy");
        var master = builder.Build();
        Assert.Same(assets.Groups[0], master.Groups[0]);
        Assert.Same(assets.Tags[0], master.Tags[0]);
        Assert.Empty(master.Validate());
        master.Groups.Clear();
        master.Tags.Clear();
        Assert.Equal(2, builder.Build().Groups.Count);
        Assert.Equal(2, builder.Build().Tags.Count);
    }

    /// <summary>Conflicting registrations and empty groups are rejected rather than silently overwritten.</summary>
    [Fact]
    public void ConflictsAreRejected()
    {
        var assets = BuildSample();
        var groupConflict = new LayerRegistrationBuilder("Other").Include(assets.Groups[0])
            .Group("Physics", group => group.Value("Default"));
        Assert.Contains("Group name 'Physics' is duplicated", Assert.Throws<InvalidOperationException>(
            () => groupConflict.Build()).Message);
        var tagConflict = new LayerRegistrationBuilder("Other").Include(assets.Tags[0]).Tag("Player");
        Assert.Throws<InvalidOperationException>(() => tagConflict.Build());
        var valueConflict = new LayerRegistrationBuilder("Other")
            .Group("Physics", group => group.Value("Default").Value("Default"));
        Assert.Throws<InvalidOperationException>(() => valueConflict.Build());
        var noDefault = new LayerRegistrationBuilder("Other").Group("Physics", _ => { });
        Assert.Throws<InvalidOperationException>(() => noDefault.Build());
    }

    /// <summary>Missing module keys, group keys, callbacks, values, tags, and asset arguments fail at registration.</summary>
    [Fact]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LayerRegistrationBuilder(" "));
        var builder = new LayerRegistrationBuilder("Test");
        Assert.Throws<ArgumentException>(() => builder.Group(" ", _ => { }));
        Assert.Throws<ArgumentNullException>(() => builder.Group("Physics", null!));
        Assert.Throws<ArgumentException>(() => builder.Tag(" "));
        Assert.Throws<ArgumentNullException>(() => builder.Include((LayerGroupAsset)null!));
        Assert.Throws<ArgumentNullException>(() => builder.Include((TagAsset)null!));
        Assert.Throws<ArgumentException>(() => builder.Group("Physics", group => group.Value(" ")));
        Assert.Empty(builder.Build().Validate());
    }

    static MasterNodeLayersAsset BuildSample() => new LayerRegistrationBuilder("Sample")
        .Group("Physics", group => group.Value("Default").Value("Water")).Tag("Player").Build();
}
