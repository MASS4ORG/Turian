namespace Gaya.Tests;

/// <summary>Brick format v2 manifests: the <c>id</c> key, roles under <c>Brick~/</c>, and where tools find each part.</summary>
public sealed class BrickLayoutTests
{
    const string V2 = """
        {
          "id": "org.mass4.contract-source",
          "version": "1.0.0",
          "roles": {
            "source": "Brick~/Source",
            "tests": ["Brick~/Tests/Unit", "Brick~/Tests/Integration"],
            "org.mass4.future": "Brick~/Future"
          },
          "bake": "auto",
          "trustedBakers": ["SHA256:LhZqbxnJxs8Z+wQ9/3Cx8SaK2T00wsy1O2LDowh8FPw"]
        }
        """;

    /// <summary>A v2 manifest reads its id from <c>id</c>, keeps unknown roles, and is written back with <c>id</c>.</summary>
    [Fact]
    public void V2ManifestsKeepTheirIdKeyAndRoles()
    {
        var manifest = PackageManifest.Parse(V2, "package.json");

        Assert.Equal("org.mass4.contract-source", manifest.Name);
        Assert.Equal(["Brick~/Tests/Unit", "Brick~/Tests/Integration"], manifest.RolePaths(BrickLayout.Tests));
        Assert.Equal(["Brick~/Future"], manifest.RolePaths("org.mass4.future"));
        Assert.Empty(manifest.RolePaths(BrickLayout.Samples));
        Assert.True(manifest.AllowsTwins);

        var written = JsonNode.Parse(manifest.ToJson())!.AsObject();
        Assert.Equal("id", written.First().Key);
        Assert.False(written.ContainsKey("name"));
        Assert.Equal("Brick~/Future", written["roles"]!["org.mass4.future"]!.GetValue<string>());
        Assert.Equal(manifest.Name, PackageManifest.Parse(manifest.ToJson(), "again").Name);
    }

    /// <summary>A role must name paths inside <c>Brick~/</c>; <c>bake</c> takes only auto or none.</summary>
    [Theory]
    [InlineData("\"roles\": { \"source\": \"Source~\" }")]
    [InlineData("\"roles\": { \"source\": \"Brick~/../Assets\" }")]
    [InlineData("\"roles\": { \"source\": \"Brick~\\\\Source\" }")]
    [InlineData("\"roles\": { \"tests\": [\"Brick~/Tests\", 3] }")]
    [InlineData("\"roles\": { \"docs\": { \"path\": \"Brick~/Docs\" } }")]
    [InlineData("\"bake\": \"sometimes\"")]
    [InlineData("\"trustedBakers\": [\"ssh-ed25519 AAAA\"]")]
    [InlineData("\"name\": \"user.mateo.other\"")]
    public void InvalidV2FieldsAreRejected(string field) =>
        Assert.Throws<PackageException>(() => PackageManifest.Parse($$"""{ "id": "user.mateo.rules", "version": "1.0.0", {{field}} }""", "package.json"));

    /// <summary>
    /// Tools find a part through its role; a v2 brick without the role uses the default under <c>Brick~/</c> and a v1
    /// brick its tilde folder.
    /// </summary>
    [Fact]
    public void PartsAreFoundThroughRoles()
    {
        var v2 = PackageManifest.Parse(V2, "package.json");
        var v1 = new PackageManifest { Name = "user.mateo.rules", Version = SemanticVersion.Parse("1.0.0") };

        Assert.True(BrickLayout.IsV2(v2));
        Assert.False(BrickLayout.IsV2(v1));
        Assert.Equal("Brick~/Source", BrickLayout.RelativePathOf(v2, BrickLayout.Source));
        Assert.Equal("Brick~/Precast", BrickLayout.RelativePathOf(v2, BrickLayout.Precast));
        Assert.Equal("Precast~", BrickLayout.RelativePathOf(v1, BrickLayout.Precast));
        Assert.Equal(Path.Combine("root", "Brick~", "Bake"), BrickLayout.PathOf("root", v2, BrickLayout.Bake));
    }
}
