using System.Runtime.Loader;

namespace Turian.Tests;

/// <summary>Presenter discovery tolerates incompatible packages while retaining usable UI factories.</summary>
[Collection(SerialTests.Name)]
public sealed class UiPresentersTests
{
    /// <summary>Core and Vulkan do not reference a GUI library or provide a presenter on their own.</summary>
    [Fact]
    public void EngineWithoutUiHasNoGuiDependencyOrPresenter()
    {
        var assemblies = new[] { typeof(Node).Assembly, typeof(Vulkan).Assembly };
        Assert.Null(UiPresenters.Find(assemblies));
        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(),
                static name => name.Name?.StartsWith("Guinevere", StringComparison.Ordinal) == true);
        }
    }

    /// <summary>An obsolete factory reference must not crash discovery or hide a later working package.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingFactoryTypeDoesNotAbortDiscovery(bool replaceDependency)
    {
        var dependencyName = "UiDependency" + Guid.NewGuid().ToString("N");
        var dependency = Compile(dependencyName, "public sealed class RemovedFactory { }");
        var stalePackage = Compile("StaleUi" + Guid.NewGuid().ToString("N"), """
            [assembly: Turian.Engine.Core.UiPresenterFactory(typeof(RemovedFactory))]
            """, MetadataReference.CreateFromImage(dependency));
        var replacement = Compile(dependencyName, "public sealed class Replacement { }");
        var validPackage = Compile("ValidUi" + Guid.NewGuid().ToString("N"), """
            [assembly: Turian.Engine.Core.UiPresenterFactory(typeof(WorkingFactory))]
            public sealed class WorkingFactory : Turian.Engine.Core.IUiPresenterFactory
            {
                public Turian.Engine.Core.IUiPresenter Create(Turian.Engine.Core.Vulkan vulkan,
                    Turian.Engine.Core.IInputSource input, System.IServiceProvider services) => null;
            }
            """);
        var context = new AssemblyLoadContext("ui-discovery-test", isCollectible: true);
        try
        {
            if (replaceDependency)
            {
                using var replacementStream = new MemoryStream(replacement);
                context.LoadFromStream(replacementStream);
            }
            using var staleStream = new MemoryStream(stalePackage);
            var stale = context.LoadFromStream(staleStream);
            using var validStream = new MemoryStream(validPackage);
            var valid = context.LoadFromStream(validStream);

            if (replaceDependency)
            {
                Assert.Throws<TypeLoadException>(() => stale.GetCustomAttribute<UiPresenterFactoryAttribute>());
            }
            else
            {
                Assert.Throws<FileNotFoundException>(() => stale.GetCustomAttribute<UiPresenterFactoryAttribute>());
            }
            Assert.Null(UiPresenters.Find([stale]));
            Assert.Equal("WorkingFactory", UiPresenters.Find([stale, valid])?.GetType().Name);
            Assert.Null(UiPresenters.Find([]));
        }
        finally
        {
            context.Unload();
        }
    }

    static byte[] Compile(string name, string source, params MetadataReference[] extraReferences)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IUiPresenterFactory).Assembly.Location)
            .Append(typeof(IInputSource).Assembly.Location)
            .Distinct()
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .Concat(extraReferences);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}
