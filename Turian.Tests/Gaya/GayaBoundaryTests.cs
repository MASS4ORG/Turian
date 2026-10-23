namespace Turian.Tests;

/// <summary>
/// Gaya is a standalone platform that happens to live in this repository. It must never depend on
/// Turian: the dependency runs one way, Turian plugins onto Gaya. These tests fail the build the
/// moment a <c>Turian</c> reference appears in <c>Gaya/</c>, so the eventual extraction into its own
/// repository stays mechanical.
/// </summary>
public class GayaBoundaryTests
{
    static readonly string[] GayaAssemblies =
        ["Gaya.Attributes", "Gaya.Sdk", "MASS4.Bricks", "Gaya.Packages.Editor", "Gaya.Host"];

    /// <summary>Generic libraries shared by Gaya and Turian: MASS4.Attributes and the Guinevere generic forms.</summary>
    static readonly string[] SharedLibraries = ["Attributes", "Autoformers"];

    /// <summary>Compiled Gaya assemblies reference no Turian assembly, directly or transitively.</summary>
    [Fact]
    public void GayaAssemblies_ReferenceNoTurianAssembly()
    {
        var offenders = GayaAssemblies
            .SelectMany(static name => AssemblyClosure.Offenders(Assembly.Load(name), IsTurian))
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{string.Join("; ", offenders)}. Gaya must not depend on Turian — " +
            "move the shared contract into Gaya.Sdk, or invert it through a service interface.");
    }

    /// <summary>The package format stays usable on its own: it needs only the shared attributes, never the workbench.</summary>
    [Fact]
    public void Bricks_DependsOnlyOnAttributes()
    {
        var offenders = AssemblyClosure.Offenders(Assembly.Load("MASS4.Bricks"), static reference =>
            reference is "Gaya.Sdk" or "Gaya.Host" or "SkiaSharp"
            || reference.StartsWith("Guinevere", StringComparison.Ordinal)
            || IsTurian(reference));

        Assert.True(offenders.Length == 0,
            $"MASS4.Bricks must stay free of the workbench and UI: {string.Join("; ", offenders)}.");
    }

    /// <summary>The generic libraries Gaya and Turian build on reference neither of them.</summary>
    [Fact]
    public void SharedLibraries_ReferenceNoGayaOrTurian()
    {
        var offenders = SharedLibraries
            .SelectMany(static name => AssemblyClosure.Offenders(Assembly.Load(name), static reference =>
                reference.StartsWith("Gaya.", StringComparison.Ordinal) || IsTurian(reference)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"Shared libraries depend on Gaya or Turian: {string.Join("; ", offenders)}.");
    }

    static bool IsTurian(string assembly) => assembly.StartsWith("Turian", StringComparison.Ordinal);

    /// <summary>No project under <c>Gaya/</c> references a Turian project or package.</summary>
    [Fact]
    public void GayaProjects_ReferenceNoTurianProjectOrPackage()
    {
        var offenders = new List<string>();

        foreach (var project in EnumerateGayaFiles("*.csproj"))
        {
            var document = XDocument.Load(project);
            var references = document.Descendants()
                .Where(static element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(static element => element.Attribute("Include")?.Value ?? string.Empty)
                .Where(static include => include.Contains("Turian", StringComparison.OrdinalIgnoreCase));

            offenders.AddRange(references.Select(include => $"{Path.GetFileName(project)} → {include}"));
        }

        Assert.True(offenders.Count == 0,
            $"Gaya projects reference Turian: {string.Join("; ", offenders)}.");
    }

    /// <summary>No source file under <c>Gaya/</c> names a Turian namespace, type or config path.</summary>
    [Fact]
    public void GayaSources_NameNoTurianNamespaceOrPath()
    {
        // Word-boundary so that unrelated words containing the token cannot trip the check.
        var turianToken = new Regex(@"\bTurian\b|\bturian\b", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var source in EnumerateGayaFiles("*.cs"))
        {
            var lines = File.ReadAllLines(source);
            for (var i = 0; i < lines.Length; i++)
            {
                if (turianToken.IsMatch(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(source)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Gaya sources name Turian:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    static IEnumerable<string> EnumerateGayaFiles(string pattern)
    {
        var gaya = Path.Combine(RepositoryRoot(), "Gaya");
        return Directory.EnumerateFiles(gaya, pattern, SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                      StringComparison.Ordinal)
                                  && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                      StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test binaries to the directory holding the solution file.</summary>
    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Turian.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Test binaries are outside the repository; source scan not applicable.");
        return directory.FullName;
    }
}
