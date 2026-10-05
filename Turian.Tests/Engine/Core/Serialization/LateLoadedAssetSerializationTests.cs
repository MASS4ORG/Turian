using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Checks asset types loaded after serializer initialization preserve their payload identity.</summary>
[Collection(SerialTests.Name)]
public sealed class LateLoadedAssetSerializationTests
{
    /// <summary>Frozen options serialize a newly loaded settings type with its TypeId and register its payload.</summary>
    [Fact]
    public void LateLoadedSettingsRetainPayloadTypeIds()
    {
        var options = Serializer.JsonOptions;
        Serializer.Serialize(new Node());
        Assert.True(options.IsReadOnly);
        var typeId = Guid.NewGuid();
        var context = new AssemblyLoadContext($"late-asset-{typeId:N}", isCollectible: true);
        var assembly = LoadSettings(context, typeId);
        try
        {
            var type = assembly.GetType("LateSettings")!;
            var settings = (DataAsset)Activator.CreateInstance(type)!;
            type.GetProperty("Enabled")!.SetValue(settings, true);
            var json = JsonSerializer.Serialize(settings, type, options);
            Assert.Contains(typeId.ToString(), json);
            var restored = Serializer.LoadData<DataAsset>(json)!;
            Assert.IsType(type, restored);
            Assert.Equal(true, type.GetProperty("Enabled")!.GetValue(restored));
            var directory = Directory.CreateTempSubdirectory("turian-late-settings-");
            try
            {
                Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
                var path = Path.Combine(directory.FullName, "Assets", "Settings.dataasset");
                File.WriteAllText(path, json);
                var database = new AssetDatabase();
                Assert.True(database.RegisterAsset(new DataAssetAsset { Id = settings.Id, RelativePath = path }, path));
                Assert.True(database.TryGetAsset(settings.Id, out var record));
                Assert.Equal(typeId, record!.DataAssetPayloadTypeId);
            }
            finally { directory.Delete(recursive: true); }
        }
        finally
        {
            CollectibleAssemblies.Release([assembly]);
            context.Unload();
        }
    }

    static Assembly LoadSettings(AssemblyLoadContext context, Guid typeId)
    {
        var source = $$"""
            [MASS4.Attributes.TypeId("{{typeId}}")]
            public sealed class LateSettings : Turian.Engine.Core.ProjectSettingsAsset
            {
                public bool Enabled { get; set; }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(DataAsset).Assembly.Location).Append(typeof(TypeIdAttribute).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var token = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create($"LateAsset{typeId:N}",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: token)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream, cancellationToken: token);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        return context.LoadFromStream(stream);
    }
}
