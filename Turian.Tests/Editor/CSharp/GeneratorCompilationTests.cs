using static Turian.Tests.GeneratorTestCompiler;

namespace Turian.Tests;

/// <summary>Compiles generator output and analyzes source without changing runtime serializer registrations.</summary>
public sealed class GeneratorCompilationTests
{
    /// <summary>Verifies that the analyzer flags node and component members on a DataAsset, and nothing else.</summary>
    [Fact]
    public async Task Analyzer_FlagsSceneObjectMembersOnDataAssets()
    {
        const string source = """
            using System.Collections.Generic;
            using Turian.Engine.Core;
            public class Holder : DataAsset
            {
                public Node Target;
                public List<CameraComponent> Cameras { get; set; }
                [Turian.SerializeInline] public Node Inline;
                public int Plain;
            }
            public class Script : Component { public Node Target; }
            """;

        var diagnostics = await Compile(source).WithAnalyzers([LoadFromGenerator<DiagnosticAnalyzer>(
                "Turian.CSharp.CodeGenerator.DataAssetSceneReferenceAnalyzer")])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Cameras", "Target"],
            diagnostics.Where(static d => d.Id == "TUR0001").Select(static d => d.GetMessage().Split('\'')[1]).Order());
    }

    /// <summary>Verifies that generated code compiles for awkward member names and shapes.</summary>
    [Fact]
    public void Generator_OutputCompiles()
    {
        const string source = """
            using System.Collections.Generic;
            using Turian.Engine.Core;
            namespace Game.Data;
            public partial class Outer
            {
                public partial class Stats : DataAsset
                {
                    public int @class;
                    public List<Stats> Chain { get; set; }
                    public Dictionary<string, int> Table { get; set; }
                    [Turian.Observable] public partial string Title { get; set; }
                }
            }
            public class Skipped : DataAsset { public int Value { get; private set; } }
            """;

        CSharpGeneratorDriver.Create(
                [LoadFromGenerator<IIncrementalGenerator>("Turian.CSharp.CodeGenerator.DataAssetGenerator")
                    .AsSourceGenerator()],
                parseOptions: GeneratorTestCompiler.ParseOptions)
            .RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _,
                TestContext.Current.CancellationToken);

        var errors = output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);

        var generated = output.SyntaxTrees.Single(static t => t.FilePath.EndsWith("TurianGeneratedSerializers.g.cs",
            StringComparison.Ordinal)).ToString();
        Assert.Contains("Register<global::Game.Data.Outer.Stats>", generated);
        Assert.DoesNotContain("Skipped", generated);
    }

}
