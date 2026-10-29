namespace Turian.Tests;

/// <summary>Verifies static export identities, module ordering and closed JSON composition.</summary>
public sealed class ExportRuntimeGeneratorTests
{
    /// <summary>A restored SDK project produces a physical composition/context file before publish.</summary>
    [Fact]
    public async Task ExportWritesCompositionFromProjectSymbols()
    {
        var root = Directory.CreateTempSubdirectory("turian-native-composition-").FullName;
        try
        {
            var assets = Directory.CreateDirectory(Path.Combine(root, "Assets")).FullName;
            var project = Path.Combine(root, "NativeFixture.csproj");
            var corePath = System.Security.SecurityElement.Escape(typeof(Component).Assembly.Location);
            var attributesPath = System.Security.SecurityElement.Escape(typeof(TypeIdAttribute).Assembly.Location);
            await File.WriteAllTextAsync(project, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Turian.Engine.Core" HintPath="{{corePath}}" />
                    <Reference Include="MASS4.Attributes" HintPath="{{attributesPath}}" />
                  </ItemGroup>
                </Project>
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(assets, "NativeProbe.cs"),
                "public sealed class NativeProbe : Turian.Engine.Core.Component { }", TestContext.Current.CancellationToken);
            var start = CompilerBase.WithoutLocatorEnvironment(new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "restore", project, "--ignore-failed-sources" }
            });
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await output + await error);
            var settings = Substitute.For<IBuildAppSettings>();
            settings.ProjectAbsoluteDir.Returns(root);
            settings.AssetsAbsoluteDir.Returns(assets);
            settings.CacheAbsoluteDir.Returns(Path.Combine(root, ".Cache"));
            settings.TitleToPathFriendly.Returns("NativeFixture");

            await ExportRuntimeGenerator.GenerateAsync(project, settings,
                new BuildProfile { Target = BuildTarget.Server, NativeAot = true }, NullLogger.Instance);

            var generated = await File.ReadAllTextAsync(Path.Combine(root, "GeneratedExportRuntime.cs"),
                TestContext.Current.CancellationToken);
            Assert.Contains("typeof(global::NativeProbe)", generated);
            Assert.Contains("partial class ExportJsonContext", generated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Manifest ids win for writing while declared ids remain readable aliases.</summary>
    [Fact]
    public void CompositionRootsBothIdsAndOrdersModulesWithoutRuntimeDiscovery()
    {
        const string source = """
            namespace Usercode;
            [MASS4.Attributes.TypeId("a5846b25-f72a-4449-8912-d601a2054e3c")]
            public sealed class Probe : Turian.Engine.Core.Component
            {
                public Turian.Engine.Core.DataAsset? Shared { get; set; }
                public System.Collections.Generic.List<Turian.Engine.Core.DataAsset> Assets { get; set; } = new();
                public int[] Samples = new int[0];
            }
            public sealed class Zeta : Turian.Engine.Core.IEngineServiceModule
            {
                public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
            }
            public sealed class Alpha : Turian.Engine.Core.IEngineServiceModule
            {
                public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(static path => !Path.GetFileName(path).StartsWith("Turian.", StringComparison.Ordinal)
                                  || Path.GetFileName(path) is "Turian.Engine.Core.dll" or "Turian.Engine.Attributes.dll");
        var compilation = CSharpCompilation.Create("GeneratedGame",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            paths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var id = Guid.Parse("f6f3dc8d-252c-43b7-8c66-e850b7191277");
        var manifest = new UserCodeTypeManifest
        {
            Types = [new UserCodeTypeEntry { FullyQualifiedName = "Usercode.Probe", TypeId = id }]
        };

        var generated = ExportRuntimeGenerator.GenerateSource(compilation, manifest);

        Assert.Contains("a5846b25-f72a-4449-8912-d601a2054e3c", generated);
        Assert.Contains(id.ToString(), generated);
        Assert.True(generated.IndexOf("a5846b25-f72a-4449-8912-d601a2054e3c", StringComparison.Ordinal)
                    < generated.IndexOf(id.ToString(), StringComparison.Ordinal));
        Assert.Contains("GeneratedObjectFactory.Register<global::Usercode.Probe>()", generated);
        Assert.True(generated.IndexOf("new global::Usercode.Alpha()", StringComparison.Ordinal)
                    < generated.IndexOf("new global::Usercode.Zeta()", StringComparison.Ordinal));
        Assert.Contains("AssetReferenceJsonConverterFactory.Register<", generated);
        Assert.Contains("JsonSerializable(typeof(global::Turian.Engine.Core.Transform))", generated);
        Assert.DoesNotContain("AppDomain", generated);
        Assert.DoesNotContain("Assembly.Load", generated);
        Assert.Empty(CSharpSyntaxTree.ParseText(generated, cancellationToken: TestContext.Current.CancellationToken)
            .GetDiagnostics(TestContext.Current.CancellationToken));
    }
}
