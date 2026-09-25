using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Turian.Tests;

/// <summary>
/// Tests the DataAsset source generator: generated serializers write exactly what reflection writes, [Observable]
/// properties raise change notifications, and the scene-reference analyzer flags node and component members.
/// </summary>
public sealed partial class DataAssetGeneratorTests
{
    /// <summary>A mode, to cover enums.</summary>
    public enum Mode
    {
        /// <summary>The first mode.</summary>
        Calm,

        /// <summary>The second mode.</summary>
        Angry,
    }

    /// <summary>A DataAsset covering the member kinds the generator handles.</summary>
    [TypeId("a4000005-0000-4000-8000-000000000001")]
    public sealed partial class Sample : DataAsset
    {
        /// <summary>A string.</summary>
        public string? Name { get; set; } = "sample \"quoted\"";

        /// <summary>An enum.</summary>
        public Mode Mode { get; set; } = Mode.Angry;

        /// <summary>A nullable value.</summary>
        public int? Optional { get; set; } = 3;

        /// <summary>A list of values.</summary>
        public List<float> Curve { get; set; } = [0.5f, 1.25f];

        /// <summary>A reference to another DataAsset.</summary>
        public Sample? Next { get; set; }

        /// <summary>References to other DataAssets.</summary>
        public Sample?[]? Others;

        /// <summary>An inline copy of another DataAsset.</summary>
        [SerializeInline]
        public Sample? Copy { get; set; }

        /// <summary>An observable value.</summary>
        [Observable]
        public partial int Health { get; set; }
    }

    /// <summary>Verifies that generated serializers write the same JSON as reflection, and read it back.</summary>
    [Theory]
    [InlineData(typeof(DataAssetTest))]
    [InlineData(typeof(GraphicsSettings))]
    [InlineData(typeof(InputSettings))]
    [InlineData(typeof(LocalizationSettings))]
    [InlineData(typeof(PlayerSettings))]
    [InlineData(typeof(InputActionsAsset))]
    [InlineData(typeof(Sample))]
    public void GeneratedSerializer_MatchesReflection(Type type)
    {
        var value = (DataAsset)Activator.CreateInstance(type)!;
        if (value is Sample sample)
        {
            sample.Next = new Sample();
            sample.Others = [new Sample(), null];
            sample.Copy = new Sample { Name = "inline" };
            sample.Health = 9;
        }

        Assert.True(GeneratedSerializers.TryGet(type, out _));
        var generated = Serializer.Serialize<DataAsset>(value);
        var generatedRoundTrip = Serializer.Serialize(Serializer.LoadData<DataAsset>(generated));

        Assert.True(GeneratedSerializers.Remove(type, out var serializer));
        try
        {
            Assert.Equal(Serializer.Serialize<DataAsset>(value), generated);
            Assert.Equal(Serializer.Serialize(Serializer.LoadData<DataAsset>(generated)), generatedRoundTrip);
        }
        finally
        {
            GeneratedSerializers.Register(type, serializer!);
        }
    }

    /// <summary>Verifies that an [Observable] property raises Changed once per actual change.</summary>
    [Fact]
    public void ObservableProperty_RaisesChangedOnChange()
    {
        var sample = new Sample();
        var changes = new List<string>();
        sample.Changed += (asset, member) =>
        {
            Assert.Same(sample, asset);
            changes.Add(member);
        };

        sample.Health = 5;
        sample.Health = 5;
        sample.Health = 6;

        Assert.Equal([nameof(Sample.Health), nameof(Sample.Health)], changes);
        Assert.Contains("\"Health\": 6", Serializer.Serialize<DataAsset>(sample));
    }

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
                parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(Compile(source), out var output, out _,
                TestContext.Current.CancellationToken);

        var errors = output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);

        var generated = output.SyntaxTrees.Single(static t => t.FilePath.EndsWith("TurianGeneratedSerializers.g.cs",
            StringComparison.Ordinal)).ToString();
        Assert.Contains("typeof(global::Game.Data.Outer.Stats)", generated);
        Assert.DoesNotContain("Skipped", generated);
    }

    static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Latest);

    static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create(
            "GeneratorTest",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    static T LoadFromGenerator<T>(string typeName)
    {
        var root = AppContext.BaseDirectory;
        while (Directory.GetFiles(root, "*.slnx").Length == 0) root = Directory.GetParent(root)!.FullName;

        var assembly = Assembly.LoadFrom(Path.Combine(root, "Turian", "Editor", "CSharp", "CodeGenerator", "bin",
            "Debug", "netstandard2.0", "Turian.CSharp.CodeGenerator.dll"));
        return (T)Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!)!;
    }
}
