namespace Turian.Tests;

/// <summary>Tests registering user types from the compiled type manifest.</summary>
public sealed class TypeRegistryManifestTests : IDisposable
{
    sealed class ManifestTarget : IdObject;

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
                { "FullyQualifiedName": "{{typeof(ManifestTarget).FullName}}", "TypeId": "{{id}}",
                  "Assembly": "{{typeof(ManifestTarget).Assembly.GetName().Name}}" },
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

    /// <summary>Identical type names in old and current assemblies resolve through the declared assembly.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DeclaredAssemblyControlsTypeResolution(bool missingAssembly, bool defaultAssembly)
    {
        const string name = "Example.DuplicateComponent";
        var stale = DefineType("Stale");
        var current = DefineType("Current");
        var id = Guid.NewGuid();
        var assemblyName = missingAssembly ? "Missing.Example" : current.Assembly.GetName().Name;
        var entry = new JsonObject { ["FullyQualifiedName"] = name, ["TypeId"] = id.ToString() };
        var manifest = new JsonObject { ["Types"] = new JsonArray(entry) };
        if (defaultAssembly) manifest["AssemblyName"] = assemblyName;
        else entry["Assembly"] = assemblyName;
        File.WriteAllText(Manifest, manifest.ToJsonString());

        TypeRegistry.RegisterFromManifest(Manifest, NullLogger.Instance);

        Assert.NotEqual(stale, current);
        Assert.Equal(!missingAssembly, TypeRegistry.TryGetType(id, out var resolved));
        Assert.Equal(missingAssembly ? null : current, resolved);

        static Type DefineType(string prefix)
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"{prefix}.{Guid.NewGuid():N}"),
                System.Reflection.Emit.AssemblyBuilderAccess.Run);
            return assembly.DefineDynamicModule("Main").DefineType(name).CreateType()!;
        }
    }
}
