namespace Turian.Tests;

/// <summary>Checks phrase-derived payload identities retain read compatibility with earlier authored assets.</summary>
public sealed class LayerTypeIdTests
{
    /// <summary>The phrase-derived payload attributes remain literal source constants and do not run a build generator.</summary>
    [Fact]
    public void PayloadTypeIdsAreSourceLiterals()
    {
        var root = AppContext.BaseDirectory;
        while (Directory.GetFiles(root, "*.slnx").Length == 0) root = Directory.GetParent(root)!.FullName;
        var files = new[] { "Asset/Layers/EnumValueAsset.cs", "Asset/Layers/LayerGroupAsset.cs",
            "Asset/Settings/NodeLayerSettings.cs" };
        var attributes = files.SelectMany(file => CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(root, "Turian/Engine/Core", file)),
            cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken)
            .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>()
            .Where(attribute => attribute.Name.ToString() == "TypeId")).ToArray();
        Assert.Equal(5, attributes.Length);
        Assert.All(attributes, attribute => Assert.True(attribute.ArgumentList!.Arguments[0].Expression
            .IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)));
    }

    /// <summary>Earlier TypeIds resolve to the same payload type while new writes use its phrase-derived identity.</summary>
    [Theory]
    [InlineData("aa0c7d85-70db-4db4-aeaa-ea91d17f7608", "35df23dd-ab7a-53ee-a580-741fa147caeb",
        typeof(EnumValueAsset))]
    [InlineData("b9ee2d03-11c2-4826-aac3-615e146972b7", "beaa551f-3c02-5add-8879-a3aa9c39b72a",
        typeof(LayerValueAsset))]
    [InlineData("9c948080-6463-4bb7-83e7-58999c5f9d94", "19ab83e0-67a6-51bb-9d57-b5e0b38a8765",
        typeof(TagAsset))]
    [InlineData("9b2c918e-3b11-4e3c-b33b-a1c2a0b20f8e", "bda16c7d-0660-5900-a068-fc33916c3654",
        typeof(LayerGroupAsset))]
    [InlineData("c2e378eb-0b0c-49aa-916c-aa9c8a219f21", "90104bcf-6e4b-559e-98cf-4c428ca1221d",
        typeof(NodeLayerSettings))]
    public void EarlierPayloadTypeIdsRemainReadable(string previousId, string currentId, Type type)
    {
        Assert.Equal(type, TypeRegistry.GetTypeOrThrow(Guid.Parse(previousId)));
        Assert.Equal(Guid.Parse(currentId), TypeRegistry.GetIdOrThrow(type));
        if (type.IsAbstract) return;
        var id = Guid.NewGuid();
        var payload = $$"""{"__TypeId":"{{previousId}}","Id":"{{id}}","Name":"Authored"}""";
        var asset = Serializer.LoadData<DataAsset>(payload)!;
        Assert.Equal(type, asset.GetType());
        Assert.Equal(id, asset.Id);
        Assert.Contains(currentId, Serializer.Serialize(asset));
        Assert.DoesNotContain(previousId, Serializer.Serialize(asset));
    }
}
