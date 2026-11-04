namespace Turian.Tests;

/// <summary>Shares immutable metadata across isolated analyzer and generator compilations.</summary>
static class GeneratorTestCompiler
{
    // Metadata images are immutable and safe to share between independent compilations.
    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))]);

    internal static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    internal static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create(
            "GeneratorTest",
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    internal static T LoadFromGenerator<T>(string typeName)
    {
        var root = AppContext.BaseDirectory;
        while (Directory.GetFiles(root, "*.slnx").Length == 0)
        {
            root = Directory.GetParent(root)!.FullName;
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var assembly = Assembly.LoadFrom(Path.Combine(root, "Turian", "Editor", "CSharp", "CodeGenerator", "bin",
            configuration, "netstandard2.0", "Turian.CSharp.CodeGenerator.dll"));
        return (T)Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!)!;
    }
}
