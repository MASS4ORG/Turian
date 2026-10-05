using static Turian.Tests.LayerTestData;

namespace Turian.Tests;

/// <summary>Checks reproducible layout fingerprints and snapshot compatibility boundaries.</summary>
public sealed class LayerLayoutTests
{
    /// <summary>The fingerprint matches the versioned byte protocol reproduced by an external implementation.</summary>
    [Fact]
    public void FingerprintHasAnExternalGoldenVector()
    {
        var first = new LayerValueAsset { Id = Id("00000101"), Name = "Default" };
        var second = new LayerValueAsset { Id = Id("00000102"), Name = "Second" };
        var settings = new NodeLayerSettings
        {
            Groups = [new LayerGroupAsset
            {
                Id = Id("00000100"), Name = "Group", Values = [first, second], DefaultValue = first,
            }],
            Tags = [new TagAsset { Id = Id("00000200"), Name = "Later" },
                new TagAsset { Id = Id("00000001"), Name = "Earlier" }],
        };
        var layout = Registry(settings);
        Assert.Equal("v2:834e8eddbc7d5dd5f39636a342f491301d8d71974b6d7986e5162795853d66ad",
            layout.Fingerprint);
        layout.RequireCompatibleLayout(layout.Fingerprint);
        Assert.Throws<InvalidDataException>(() => layout.RequireCompatibleLayout("v1:" + layout.Fingerprint[3..]));
        Assert.Throws<InvalidDataException>(() => layout.RequireCompatibleLayout(null!));
        Assert.Equal(Registry(null).Fingerprint, Registry(new NodeLayerSettings()).Fingerprint);
    }

    /// <summary>Presentation and tag encounter order do not change indices; changes to slot identities refuse binding.</summary>
    [Fact]
    public void FingerprintTracksLayoutRatherThanPresentation()
    {
        var settings = TypedSettings();
        var group = settings.Groups[0];
        var original = Registry(settings);
        group.Name = "Renamed";
        group.Values[1].Name = "Sea";
        group.Values[1].Color = new Color32(1, 2, 3);
        settings.Tags[0].Description = "Metadata";
        original.RequireCompatibleLayout(Registry(settings).Fingerprint);

        settings.Tags.Add(new TagAsset { Name = "Extra" });
        var tagged = Registry(settings);
        settings.Tags.Reverse();
        tagged.RequireCompatibleLayout(Registry(settings).Fingerprint);
        Assert.Throws<InvalidDataException>(() => original.RequireCompatibleLayout(tagged.Fingerprint));
        settings.Tags.RemoveAll(tag => tag.Name == "Extra");

        group.Values.Reverse();
        AssertDifferent(original, settings);
        group.Values.Reverse();
        group.DefaultValue = group.Values[1];
        AssertDifferent(original, settings);
        group.DefaultValue = group.Values[0];
        group.Values.Insert(1, new LayerValueAsset { Name = "Inserted" });
        AssertDifferent(original, settings);
        group.Values.RemoveAt(1);
        settings.Groups.Reverse();
        AssertDifferent(original, settings);
        settings.Groups.Reverse();
        group.Values[1].Id = Guid.NewGuid();
        AssertDifferent(original, settings);
        Assert.Equal(1, original.GetLayerCount(1));
        Assert.Equal(Guid.Parse("d5f74ca8-a970-4770-a2c6-893d27000101"), original.GetLayerId(0, 1));
    }

    /// <summary>GUID field ordering matches canonical text and differs from mixed-endian byte-array ordering.</summary>
    [Fact]
    public void GuidOrderingUsesCanonicalFields()
    {
        var smaller = Id("00000001");
        var larger = Id("00000100");
        Assert.True(smaller.CompareTo(larger) < 0);
        Assert.True(StringComparer.Ordinal.Compare(smaller.ToString("D"), larger.ToString("D")) < 0);
        Assert.True(smaller.ToByteArray().AsSpan().SequenceCompareTo(larger.ToByteArray()) > 0);
        var ids = new[] { larger, smaller, Id("80000000"), Id("7fffffff") };
        Assert.Equal(ids.Order().Select(id => id.ToString("D")), ids.Select(id => id.ToString("D")).Order());
    }

    /// <summary>Consumer bindings participate in compatibility checks and do not follow later asset edits.</summary>
    [Fact]
    public void ConsumerBindingsArePartOfTheSnapshot()
    {
        var settings = TypedSettings();
        var unbound = Registry(settings);
        Assert.Equal(Guid.Empty, unbound.PhysicsGroupId);
        Assert.Equal(Guid.Empty, unbound.RenderingGroupId);
        settings.PhysicsGroup = settings.Groups[0];
        settings.RenderingGroup = settings.Groups[1];
        var bound = Registry(settings);
        Assert.Equal(settings.PhysicsGroup.Id, bound.PhysicsGroupId);
        Assert.Equal(settings.RenderingGroup.Id, bound.RenderingGroupId);
        Assert.NotEqual(unbound.Fingerprint, bound.Fingerprint);
        settings.PhysicsGroup = settings.Groups[1];
        AssertDifferent(bound, settings);
        Assert.Equal(settings.Groups[0].Id, bound.PhysicsGroupId);
        settings.PhysicsGroup = settings.Groups[0];
        settings.RenderingGroup = settings.Groups[0];
        AssertDifferent(bound, settings);
        settings.PhysicsGroup = Group("Unregistered");
        Assert.Contains(settings.Validate(), error => error.Contains("Physics group", StringComparison.Ordinal));
        settings.PhysicsGroup = null;
        settings.RenderingGroup = Group("Unregistered");
        Assert.Throws<InvalidOperationException>(() => Registry(settings));
    }

    static Guid Id(string prefix) => Guid.Parse(prefix + "-0000-0000-0000-000000000000");

    static void AssertDifferent(LayerInterningService original, NodeLayerSettings settings)
    {
        var current = Registry(settings);
        Assert.NotEqual(original.Fingerprint, current.Fingerprint);
        Assert.Throws<InvalidDataException>(() => original.RequireCompatibleLayout(current.Fingerprint));
    }
}
