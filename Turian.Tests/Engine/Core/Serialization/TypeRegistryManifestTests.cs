namespace Turian.Tests;

/// <summary>Tests registering user types from the compiled type manifest.</summary>
public sealed class TypeRegistryManifestTests : IDisposable
{
    sealed class ManifestTarget : IdClass;

    readonly string directory = Directory.CreateTempSubdirectory("turian-manifest-").FullName;

    string Manifest => Path.Combine(directory, "types.json");

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>Valid entries register; malformed and unknown ones are skipped without failing the rest.</summary>
    [Fact]
    public void RegistersValidEntriesAndSkipsTheRest()
    {
        var id = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, "native.dll"), "not an assembly");
        File.WriteAllText(Manifest, $$"""
            {
              "AssemblyName": "Turian.Missing.UserCode",
              "Types": [
                { "FullyQualifiedName": "{{typeof(ManifestTarget).FullName}}", "TypeId": "{{id}}" },
                { "FullyQualifiedName": "Nowhere.Unknown", "TypeId": "{{unknownId}}" },
                { "FullyQualifiedName": "Nowhere.BadId", "TypeId": "not-a-guid" },
                { "TypeId": "{{Guid.NewGuid()}}" }
              ]
            }
            """);

        TypeRegistry.RegisterFromManifest(Manifest, NullLogger.Instance);

        Assert.True(TypeRegistry.TryGetType(id, out var type));
        Assert.Equal(typeof(ManifestTarget), type);
        Assert.False(TypeRegistry.TryGetType(unknownId, out _));
    }

    /// <summary>A missing, unreadable or type-less manifest registers nothing and does not throw.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public void UnusableManifestIsIgnored(string? content)
    {
        if (content is not null) File.WriteAllText(Manifest, content);
        var count = TypeRegistry.Count;

        TypeRegistry.RegisterFromManifest(Manifest, NullLogger.Instance);

        Assert.Equal(count, TypeRegistry.Count);
    }
}
