namespace Turian.Editor.Core;

/// <summary>The dependency graph resolved by MSBuild for the game launcher shipped with this editor.</summary>
public sealed class RuntimeDependencies
{
    /// <summary>The graph of the desktop launcher this editor ships, the default platform's.</summary>
    internal static RuntimeDependencies Current { get; } = Load();

    /// <summary>The target framework the launcher and its engine assemblies build for.</summary>
    public string TargetFramework { get; private set; } = string.Empty;

    /// <summary>The NuGet packages and versions the launcher resolves.</summary>
    public IReadOnlyList<(string, string)> Packages { get; private set; } = [];

    /// <summary>The runtime assemblies as (source folder, assembly name) pairs.</summary>
    public IReadOnlyList<(string, string)> Assemblies { get; private set; } = [];

    /// <summary>The runtime project paths relative to the checkout root, without file extensions.</summary>
    public IReadOnlyList<string> Projects { get; private set; } = [];

    static RuntimeDependencies Load()
    {
        using var stream = typeof(RuntimeDependencies).Assembly.GetManifestResourceStream("Turian.RuntimeDependencies")
            ?? throw new InvalidOperationException("The editor build is missing its runtime dependency manifest.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    internal static RuntimeDependencies Parse(TextReader reader)
    {
        var result = new RuntimeDependencies();
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var projects = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            ReadEntry(line, result, packages, assemblies, projects);

        if (string.IsNullOrEmpty(result.TargetFramework) || packages.Count == 0 || assemblies.Count == 0)
            throw new InvalidDataException("The runtime dependency manifest is incomplete.");
        result.SetReferences(packages, assemblies, projects);
        return result;
    }

    void SetReferences(Dictionary<string, string> packages, Dictionary<string, string> assemblies,
        HashSet<string> projects)
    {
        Packages = [.. packages.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => (item.Key, item.Value))];
        Assemblies = [.. assemblies.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => (item.Value, item.Key))];
        Projects = [.. projects.Order(StringComparer.Ordinal)];
    }

    static void ReadEntry(string line, RuntimeDependencies result, Dictionary<string, string> packages,
        Dictionary<string, string> assemblies, HashSet<string> projects)
    {
        var fields = line.Split('|');
        switch (fields[0])
        {
            case "framework":
                RequireFieldCount(fields, 2);
                result.TargetFramework = fields[1];
                break;
            case "package":
                RequireFieldCount(fields, 3);
                packages.Add(fields[1], fields[2]);
                break;
            case "assembly":
                RequireFieldCount(fields, 4);
                assemblies.Add(fields[2], fields[1].Replace('\\', '/'));
                if (fields[3].Length != 0) projects.Add(Path.ChangeExtension(fields[3].Replace('\\', '/'), null));
                break;
            default:
                throw new InvalidDataException($"Invalid runtime dependency manifest entry: '{line}'.");
        }
    }

    static void RequireFieldCount(string[] fields, int count)
    {
        if (fields.Length != count)
            throw new InvalidDataException($"Invalid runtime dependency manifest entry: '{string.Join('|', fields)}'.");
    }
}
