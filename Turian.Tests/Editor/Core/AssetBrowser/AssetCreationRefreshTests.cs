using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Creation discovery follows live assemblies across project changes.</summary>
[Collection(SerialTests.Name)]
public sealed class AssetCreationRefreshTests
{
    /// <summary>Unreadable attribute metadata skips the affected type without breaking the creation menu.</summary>
    [Fact]
    public void DiscoverySkipsUnreadableTypeMetadata()
    {
        var type = new UnreadableType();
        Assert.Empty(AssetCreationCatalog.Scan([new TestAssembly(type)], Substitute.For<ILogger>()));
        Assert.Equal(1, type.Reads);
    }

    sealed class TestAssembly(Type type) : Assembly
    {
        public override Type[] GetTypes() => [type];
    }

    sealed class UnreadableType() : TypeDelegator(typeof(DataAssetTest))
    {
        internal int Reads;
        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            Reads++;
            throw new TypeLoadException("Unreadable asset metadata");
        }
    }

    /// <summary>Old collectible project assemblies are excluded even when their names differ from the new project.</summary>
    [Fact]
    public void DiscoveryExcludesPreviousProjectAndUnloadedAssemblies()
    {
        var previous = new AssemblyLoadContext("previous-project", isCollectible: true);
        var current = new AssemblyLoadContext("current-project", isCollectible: true);
        try
        {
            var oldAssembly = LoadProject(previous, "PreviousProject");
            var newAssembly = LoadProject(current, "CurrentProject");
            var loaded = new[] { oldAssembly, newAssembly, typeof(DataAsset).Assembly };

            Assert.DoesNotContain(oldAssembly, AssetCreationCatalog.CandidateAssemblies(loaded, [newAssembly]));
            Assert.DoesNotContain(oldAssembly, AssetCreationCatalog.CandidateAssemblies(loaded, []));
            Assert.Contains(newAssembly, AssetCreationCatalog.CandidateAssemblies(loaded, [newAssembly]));
            Assert.Contains(typeof(DataAsset).Assembly, AssetCreationCatalog.CandidateAssemblies(loaded, []));
        }
        finally
        {
            previous.Unload();
            current.Unload();
        }
    }

    static Assembly LoadProject(AssemblyLoadContext context, string name)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(DataAsset).Assembly.Location)
            .Distinct()
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText($$"""
                [Turian.CreateAssetMenu("Tests/{{name}}")]
                public class ProjectData : Turian.Engine.Core.DataAsset { }
                """)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        return context.LoadFromStream(stream);
    }

    /// <summary>Changing projects immediately drops cached creation types before another menu is opened.</summary>
    [Fact]
    public void ProjectSwitchClearsCachedEntries()
    {
        var project = Directory.CreateTempSubdirectory("turian-menu-").FullName;
        try
        {
            var settings = new SettingsService();
            var app = new AppSettings { ProjectAbsoluteDir = project };
            settings.Set(app);
            var log = Substitute.For<ILogger>();
            using var build = new BuildManager(app, log);
            using var importer = new AssetImporter(log, new AssetDatabase(), settings, build);
            importer.StopMonitoring();
            using var catalog = new AssetCreationCatalog(new AssetFileSystem(settings, importer), settings, build, log);
            var entries = catalog.Kinds;
            Assert.NotEmpty(entries);

            var added = LoadProject(AssemblyLoadContext.Default, "Menu" + Guid.NewGuid().ToString("N"));
            Assert.Contains(catalog.Kinds, kind => kind.PayloadType?.Assembly == added);

            build.UpdateSettings(new AppSettings { ProjectAbsoluteDir = Path.Combine(project, "other") });

            Assert.Empty(entries);
            Assert.Contains(catalog.Kinds, static kind => kind.MenuPath == "Folder");
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }
}
