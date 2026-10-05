namespace Turian.Tests;

/// <summary>Checks authored manifests, stable references, and invalid layout diagnostics.</summary>
public sealed class LayerAssetsTests
{
    /// <summary>An empty master is a valid project with no layer groups or tags.</summary>
    [Fact]
    public void EmptyMasterIsValid() => Assert.Empty(new MasterNodeLayersAsset().Validate());

    /// <summary>Group values and defaults are saved as shared references and resolve through the asset loader.</summary>
    [Fact]
    public async Task ManifestsRoundTripAsReferences()
    {
        var value = new LayerValueAsset
        {
            Name = "Water",
            Color = new Color32(1, 2, 3),
            Description = "Water surfaces",
        };
        var group = new LayerGroupAsset { Name = "Physics", Values = [value], DefaultValue = value };
        var tag = new TagAsset { Name = "Player" };
        var master = new MasterNodeLayersAsset { Groups = [group], Tags = [tag] };
        var json = Serializer.Serialize<DataAsset>(master);
        Assert.Contains($"\"$ref\": \"{group.Id}\"", json);
        Assert.DoesNotContain("Physics", json);
        var loaded = Assert.IsType<MasterNodeLayersAsset>(Serializer.LoadData<DataAsset>(json));
        Assert.Contains(loaded.Validate(), error => error.Contains("unresolved", StringComparison.Ordinal));
        Assert.Contains(group.Id.ToString(), Serializer.Serialize<DataAsset>(loaded));

        var loadedGroup = Assert.IsType<LayerGroupAsset>(
            Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(group)));
        var loadedValue = Assert.IsType<LayerValueAsset>(
            Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(value)));
        var loadedTag = Assert.IsType<TagAsset>(Serializer.LoadData<DataAsset>(Serializer.Serialize<DataAsset>(tag)));
        Assert.Equal(value.Id, loadedValue.Id);
        Assert.Equal(value.Color, loadedValue.Color);
        Assert.Equal(value.Description, loadedValue.Description);
        Assert.Equal(tag.Id, loadedTag.Id);
        Assert.Equal(tag.Name, loadedTag.Name);

        var loader = Substitute.For<IAssetLoader>();
        loader.LoadContentAsync<DataAsset>(group.Id).Returns(loadedGroup);
        loader.LoadContentAsync<DataAsset>(value.Id).Returns(loadedValue);
        loader.LoadContentAsync<DataAsset>(tag.Id).Returns(loadedTag);
        await ObjectReferences.ResolveAsync(loadedGroup, loader);
        await ObjectReferences.ResolveAsync(loaded, loader);
        Assert.Same(loadedGroup, loaded.Groups[0]);
        Assert.Same(loadedValue, loadedGroup.Values[0]);
        Assert.Same(loadedValue, loadedGroup.DefaultValue);
        Assert.Same(loadedTag, loaded.Tags[0]);
        Assert.Empty(loaded.Validate());
    }

    /// <summary>Unresolved and absent manifests are diagnosed before any runtime indices are assigned.</summary>
    [Fact]
    public void MissingReferencesAreRejected()
    {
        var master = new MasterNodeLayersAsset { Groups = [null!], Tags = [null!] };
        Assert.Equal(2, master.Validate().Count);
        master.Groups = [new LayerGroupAsset { Name = "Physics", Values = [null!] }];
        Assert.Contains(master.Validate(), error => error.Contains("unresolved value", StringComparison.Ordinal));
        Assert.Contains(master.Validate(), error => error.Contains("default", StringComparison.Ordinal));
        master.Groups[0].Values = null!;
        Assert.Contains(master.Validate(), error => error.Contains("manifest cannot be null", StringComparison.Ordinal));
        master.Groups = null!;
        Assert.Contains(master.Validate(), error => error.Contains("manifests cannot be null", StringComparison.Ordinal));
        master.Groups = [];
        master.Tags = null!;
        Assert.Contains(master.Validate(), error => error.Contains("manifests cannot be null", StringComparison.Ordinal));
    }

    /// <summary>A default from another group is invalid even if it has the same display name.</summary>
    [Fact]
    public void DefaultMustBelongToGroup()
    {
        var value = new LayerValueAsset { Name = "Default" };
        var group = new LayerGroupAsset
        {
            Name = "Physics",
            Values = [value],
            DefaultValue = new LayerValueAsset { Name = "Default" },
        };
        var master = new MasterNodeLayersAsset { Groups = [group] };
        Assert.Contains(master.Validate(), error => error.Contains("default must reference", StringComparison.Ordinal));
        group.DefaultValue = value;
        Assert.Empty(master.Validate());
    }

    /// <summary>Names are exact and nonempty, with unique groups, unique tags, and unique values per group.</summary>
    [Fact]
    public void DuplicateAndEmptyNamesAreRejected()
    {
        var value = new LayerValueAsset { Name = "Default" };
        var group = new LayerGroupAsset
        {
            Name = "Physics",
            Values = [value, new LayerValueAsset { Name = "Default" }],
            DefaultValue = value,
        };
        var master = new MasterNodeLayersAsset
        {
            Groups = [group, new LayerGroupAsset { Name = "Physics" }, new LayerGroupAsset { Name = " " }],
            Tags = [new TagAsset { Name = "Player" }, new TagAsset { Name = "Player" }, new TagAsset()],
        };
        var errors = master.Validate();
        Assert.Contains(errors, error => error.Contains("value name 'Default' is duplicated", StringComparison.Ordinal));
        Assert.Contains(errors, error => error == "Group name 'Physics' is duplicated.");
        Assert.Contains(errors, error => error == "Group name cannot be empty.");
        Assert.Contains(errors, error => error == "Tag name 'Player' is duplicated.");
        Assert.Contains(errors, error => error == "Tag name cannot be empty.");
        value.Name = "";
        Assert.Contains(master.Validate(), error => error.Contains("value name cannot be empty", StringComparison.Ordinal));
    }

    /// <summary>Identities are nonempty and unique across all groups, values, tags, and the master.</summary>
    [Fact]
    public void DuplicateAndEmptyIdentitiesAreRejected()
    {
        var value = new LayerValueAsset { Name = "Default" };
        var group = new LayerGroupAsset { Name = "Physics", Values = [value], DefaultValue = value };
        var master = new MasterNodeLayersAsset
        {
            Groups = [group],
            Tags = [new TagAsset { Id = value.Id, Name = "Player" }, new TagAsset { Name = "Enemy" }],
        };
        Assert.Contains(master.Validate(), error => error.Contains("already used by", StringComparison.Ordinal));
        master.Tags[0].Id = Guid.Empty;
        group.Id = Guid.Empty;
        value.Id = Guid.Empty;
        master.Id = Guid.Empty;
        Assert.Equal(4, master.Validate().Count(error => error.Contains("identity cannot be empty", StringComparison.Ordinal)));
    }

    /// <summary>Capacity violations are explicit validation failures rather than truncated manifests.</summary>
    [Fact]
    public void OversizedManifestsAreRejected()
    {
        var builder = new LayerRegistrationBuilder("Capacity");
        for (var i = 0; i < 17; i++) builder.Group($"Group{i}", group => group.Value("Default"));
        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("Groups count 17 exceeds capacity 16", exception.Message);
        var values = Enumerable.Range(0, 257).Select(i => new LayerValueAsset { Name = $"Value{i}" }).ToList();
        var master = new MasterNodeLayersAsset
        {
            Groups = [new LayerGroupAsset { Name = "Physics", Values = values, DefaultValue = values[0] }],
            Tags = [.. Enumerable.Range(0, 65_537).Select(i => new TagAsset { Name = $"Tag{i}" })],
        };
        Assert.Equal(2, master.Validate().Count);
    }
}
