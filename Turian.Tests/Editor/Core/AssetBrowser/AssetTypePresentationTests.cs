namespace Turian.Tests.Editor;

/// <summary>Payload appearances stay in Studio and work across typed browsing, malformed files and generic bases.</summary>
public sealed class AssetTypePresentationTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "turian-presentations-" + Guid.NewGuid().ToString("N"));
    readonly AssetTypeCatalog catalog = new();

    /// <summary>Removes the disposable loose asset fixtures.</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Closest concrete registrations override generic families without changing extension activation.</summary>
    [Fact]
    public void ProviderAndConcreteAppearancesPreserveExtensionRules()
    {
        var provider = Substitute.For<IAssetPresentationProvider>();
        var family = Presentation(typeof(PresentationBase<>), "family");
        provider.Presentations.Returns(new[] { family });
        var registered = new AssetTypeCatalog([provider]);
        Assert.Same(family, registered.PresentationFor(typeof(PresentationAsset)));
        var concrete = Presentation(typeof(PresentationAsset), "concrete");
        registered.Register(concrete);
        Assert.Same(concrete, registered.PresentationFor(typeof(PresentationAsset)));
        Assert.Contains(registered.Types, static type => type.Id == "concrete");
        Assert.Equal(AssetActivation.Inspect, registered.ActivationFor("test.dataasset"));
        Assert.Null(registered.PresentationFor(typeof(Node)));
        Assert.Null(registered.PresentationFor((Type?)null));
        var texture = Presentation(typeof(TextureAsset), "texture");
        registered.Register(texture);
        var imported = new AssetEntry("picture.png", false, new TextureAsset(), directory);
        Assert.Same(texture, registered.PresentationFor(imported));
        Assert.Equal("texture", registered.Resolve(imported)?.Id);
    }

    /// <summary>Type filters distinguish serialized payloads while the broad Data Asset filter still includes them.</summary>
    [Fact]
    public void TypedSearchUsesPayloadIdentityAndRefreshesChangedFiles()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "example.dataasset");
        catalog.Register(Presentation(typeof(PresentationAsset), "typed"));
        catalog.Register(Presentation(typeof(PresentationBase<>), "family"));
        TypeRegistry.Register(Guid.Parse("920661cc-6567-43ca-a381-38183f932135"), typeof(PresentationAsset));
        File.WriteAllText(path, Serializer.Serialize<DataAsset>(new PresentationAsset()));
        var entry = new AssetEntry(path, false, null, directory);
        Assert.Equal("typed", catalog.Resolve(entry)?.Id);
        Assert.Equal("typed", catalog.Resolve(entry)?.Id);
        Assert.True(AssetQuery.Matches(entry, new AssetFilter { Types = new HashSet<string> { "typed" } }, catalog,
            new HashSet<string>()));
        Assert.True(AssetQuery.Matches(entry, new AssetFilter { Types = new HashSet<string> { "turian.dataAsset" } }, catalog,
            new HashSet<string>()));
        Assert.True(AssetQuery.Matches(entry, new AssetFilter { Types = new HashSet<string> { "family" } }, catalog,
            new HashSet<string>()));
        Assert.False(AssetQuery.Matches(entry, new AssetFilter { Types = new HashSet<string> { "unrelated" } }, catalog,
            new HashSet<string>()));
        File.WriteAllText(path, "{}");
        Assert.Equal("turian.dataAsset", catalog.Resolve(entry)?.Id);
    }

    /// <summary>Unregistered, missing and invalid payloads keep the ordinary file icon instead of breaking a browser frame.</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"__TypeId\":false}")]
    [InlineData("{\"__TypeId\":\"not-a-guid\"}")]
    [InlineData("{\"__TypeId\":\"da593020-a1e1-4619-8be4-78c5b96cb30a\"}")]
    public void InvalidOrUnknownPayloadsFallBack(string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "invalid.dataasset");
        catalog.Register(Presentation(typeof(PresentationAsset), "typed"));
        File.WriteAllText(path, json);
        Assert.Null(catalog.PresentationFor(new AssetEntry(path, false, null, directory)));
        Assert.Null(catalog.PresentationFor(new AssetEntry(path, true, null, directory)));
        File.Delete(path);
        Assert.Null(catalog.PresentationFor(new AssetEntry(path, false, null, directory)));
        Assert.Null(catalog.PresentationFor(new AssetEntry("file.txt", false, null, directory)));
    }

    static AssetTypePresentation Presentation(Type type, string id) =>
        new(type, id, id, "ddd.variable", new Color32(40, 180, 130), "DDD/Variable") { Glyph = "Ⓥ" };
}

/// <summary>A generic family used to verify editor type presentation fallback.</summary>
public class PresentationBase<T> : DataAsset;

/// <summary>A closed payload with stable identity for browser search fixtures.</summary>
[TypeId("920661cc-6567-43ca-a381-38183f932135")]
public sealed class PresentationAsset : PresentationBase<int>;
