using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static Turian.Tests.LayerTestData;

namespace Turian.Tests;

/// <summary>Checks typed registration, explicit identities, and asset composition.</summary>
public sealed class LayerRegistrationTests
{
    /// <summary>Code registrations use marker TypeIds directly and tolerate code renames.</summary>
    [Fact]
    public void TypeIdsAreRegistrationIdentities()
    {
        var settings = TypedSettings();
        Assert.Equal(Guid.Parse("d5f74ca8-a970-4770-a2c6-893d27000100"), settings.Groups[0].Id);
        Assert.Equal(Guid.Parse("d5f74ca8-a970-4770-a2c6-893d27000101"), settings.Groups[0].Values[1].Id);
        Assert.Equal(Guid.Parse("d5f74ca8-a970-4770-a2c6-893d27000300"), settings.Tags[0].Id);
        var renamed = new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default().Value<RenamedWater>()).Build();
        Assert.Equal(settings.Groups[0].Values[1].Id, renamed.Groups[0].Values[1].Id);
        Assert.NotEqual(settings.Groups[0].Values[1].Name, renamed.Groups[0].Values[1].Name);
        Assert.Equal(settings.Groups[0].DefaultValue!.Id, renamed.Groups[0].DefaultValue!.Id);
    }

    /// <summary>Display names and colors are independent of typed value and tag identities.</summary>
    [Fact]
    public void PresentationIsIndependentOfIdentity()
    {
        var settings = new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default().Value<Water>("Sea", new Color32(10, 20, 30), "Water surfaces"),
                "Physics presentation")
            .Tag<Player>("Hero", new Color32(40, 50, 60), "Playable actors").Build();
        var original = TypedSettings();
        var value = settings.Groups[0].Values[1];
        Assert.Equal(original.Groups[0].Id, settings.Groups[0].Id);
        Assert.Equal("Physics presentation", settings.Groups[0].Name);
        Assert.Equal(original.Groups[0].Values[1].Id, value.Id);
        Assert.Equal("Sea", value.Name);
        Assert.Equal(new Color32(10, 20, 30), value.Color);
        Assert.Equal("Water surfaces", value.Description);
        Assert.Equal(original.Tags[0].Id, settings.Tags[0].Id);
        Assert.Equal("Hero", settings.Tags[0].Name);
        Assert.Equal(new Color32(40, 50, 60), settings.Tags[0].Color);
        Assert.Equal("Playable actors", settings.Tags[0].Description);
    }

    /// <summary>Defaults may be explicit typed values and do not depend on declaration order.</summary>
    [Fact]
    public void TypedDefaultIsExplicit()
    {
        var settings = new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Value<Actors>().Default<Water>()).Build();
        Assert.Same(settings.Groups[0].Values[1], settings.Groups[0].DefaultValue);
        Assert.Equal(settings.Groups[0].DefaultValue!.Id, Registry(settings).GetLayerId(0, 0));
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default().Default()));
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default<Water>().Default<Actors>()));
    }

    /// <summary>Independent assets with shared display names compose and builder lists remain independently editable.</summary>
    [Fact]
    public void AssetAndCodeRegistrationsCompose()
    {
        var assetGroup = LayerTestData.Group();
        var assetTag = new TagAsset { Name = "Player" };
        var builder = new LayerRegistrationBuilder().Include(assetGroup).Include(assetTag)
            .Group<Physics>(group => group.Default().Value<Water>()).Tag<Player>();
        var settings = builder.Build();
        Assert.Same(assetGroup, settings.Groups[0]);
        Assert.Same(assetTag, settings.Tags[0]);
        Assert.Empty(settings.Validate());
        settings.Groups.Clear();
        settings.Tags.Clear();
        Assert.Equal(2, builder.Build().Groups.Count);
        Assert.Equal(2, builder.Build().Tags.Count);
    }

    /// <summary>Repeated registrations from one source and groups without defaults are rejected.</summary>
    [Fact]
    public void ConflictingIdentitiesAreRejected()
    {
        var assets = TypedSettings();
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder().Include(assets.Groups[0])
            .Include(assets.Groups[0]).Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder().Include(assets.Tags[0])
            .Include(assets.Tags[0]).Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder().Tag<Player>().Tag<Player>().Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default()).Group<Physics>(group => group.Default()).Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Default().Value<Water>().Value<RenamedWater>()).Build());
        Assert.Throws<InvalidOperationException>(() => new LayerRegistrationBuilder()
            .Group<Physics>(group => group.Value<Water>()).Build());
    }

    /// <summary>Unannotated keys and missing arguments are diagnosed without deriving identities from type names.</summary>
    [Fact]
    public void MissingTypeIdsAndArgumentsAreRejected()
    {
        var builder = new LayerRegistrationBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.Group<Physics>(null!));
        Assert.Throws<ArgumentNullException>(() => builder.Include((LayerGroupAsset)null!));
        Assert.Throws<ArgumentNullException>(() => builder.Include((TagAsset)null!));
        Assert.Throws<ArgumentNullException>(() => builder.Include((LayerValueAsset)null!));
        Assert.Throws<InvalidOperationException>(() => builder.Group<MissingGroupId>(group => group.Default()));
        Assert.Throws<InvalidOperationException>(() => builder.Group<Physics>(group => group.Value<MissingValueId>()));
        Assert.Throws<InvalidOperationException>(() => builder.Tag<MissingTagId>());
        Assert.Empty(builder.Build().Validate());
    }

    /// <summary>Typed constraints reject values registered in another group at compile time.</summary>
    [Fact]
    public void WrongGroupDoesNotCompile()
    {
        const string source = """
            using Turian.Engine.Core;
            class Physics : ILayerGroupKey;
            class Gameplay : ILayerGroupKey;
            class Water : ILayerValueKey<Physics>;
            class Registration
            {
                void Build() => new LayerRegistrationBuilder()
                    .Group<Gameplay>(group => group.Default().Value<Water>());
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(LayerRegistrationBuilder).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var token = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create("LayerConstraints",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: token)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(compilation.GetDiagnostics(token), diagnostic => diagnostic.Id == "CS0311");
        var valid = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source.Replace("Group<Gameplay>", "Group<Physics>"), cancellationToken: token));
        Assert.DoesNotContain(valid.GetDiagnostics(token), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
