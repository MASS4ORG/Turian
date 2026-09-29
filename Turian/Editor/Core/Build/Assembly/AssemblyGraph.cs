namespace Turian.Editor.Core;

/// <summary>One assembly of a project: the default one or an <see cref="AssemblyDefinition"/>.</summary>
/// <param name="Name">The assembly name.</param>
/// <param name="RootNamespace">The root namespace of its generated project.</param>
/// <param name="Directory">The folder it owns; the <c>Assets</c> folder for the default assembly.</param>
/// <param name="DefinitionPath">The definition's source file; null for the default assembly.</param>
/// <param name="References">Names of the project assemblies it compiles against.</param>
/// <param name="EditorOnly">Compiled and loaded by the editor only.</param>
/// <param name="AllowUnsafeCode">Allows <c>unsafe</c> code.</param>
public sealed record ProjectAssembly(
    string Name,
    string RootNamespace,
    string Directory,
    string? DefinitionPath,
    IReadOnlyList<string> References,
    bool EditorOnly,
    bool AllowUnsafeCode)
{
    /// <summary>Whether this is the default assembly, which holds every script outside a definition.</summary>
    public bool IsDefault => DefinitionPath is null;
}

/// <summary>
/// The assemblies a project's scripts compile into, read from the <see cref="AssemblyDefinition"/> files under
/// <c>Assets</c>. Nothing needs importing: a build reads the definitions straight from the sources.
/// </summary>
public sealed class AssemblyGraph
{
    static readonly Guid DefinitionTypeId = Guid.Parse(AssemblyDefinition.TypeIdValue);

    readonly Dictionary<string, ProjectAssembly> byDirectory;

    AssemblyGraph(ProjectAssembly defaultAssembly, IReadOnlyList<ProjectAssembly> definitions)
    {
        Default = defaultAssembly;
        Definitions = definitions;
        byDirectory = definitions.ToDictionary(static d => d.Directory, StringComparer.Ordinal);
    }

    /// <summary>The assembly holding every script outside a definition.</summary>
    public ProjectAssembly Default { get; }

    /// <summary>The defined assemblies, each after the ones it references.</summary>
    public IReadOnlyList<ProjectAssembly> Definitions { get; }

    /// <summary>Every assembly, each after the ones it references; the default one last.</summary>
    public IEnumerable<ProjectAssembly> All => Definitions.Append(Default);

    /// <summary>The source files of the definitions, which change what compiles where.</summary>
    public IEnumerable<string> DefinitionFiles => Definitions.Select(static d => d.DefinitionPath!);

    /// <summary>The assembly a script compiles into: the definition in its closest folder, else the default.</summary>
    /// <param name="scriptPath">The script's path.</param>
    /// <returns>The owning assembly.</returns>
    public ProjectAssembly AssemblyFor(string scriptPath)
    {
        for (var directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath));
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            if (byDirectory.TryGetValue(directory, out var owner)) return owner;
        }

        return Default;
    }

    /// <summary>The folders under <paramref name="assembly"/>'s own that belong to another definition.</summary>
    /// <param name="assembly">The assembly whose sources are collected.</param>
    /// <returns>Absolute folder paths.</returns>
    public IEnumerable<string> ExcludedDirectories(ProjectAssembly assembly) =>
        Definitions.Where(d => d != assembly && IsUnder(d.Directory, assembly.Directory))
            .Select(static d => d.Directory);

    /// <summary>Reads the definitions under a project's <c>Assets</c> folder and checks they form a valid graph.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="defaultName">The name of the default assembly.</param>
    /// <returns>The project's assemblies.</returns>
    /// <exception cref="InvalidOperationException">The definitions conflict, reference unknown assemblies or form a cycle.</exception>
    public static AssemblyGraph Discover(string assetsDirectory, string defaultName)
    {
        ArgumentNullException.ThrowIfNull(defaultName);

        var root = string.IsNullOrWhiteSpace(assetsDirectory) ? string.Empty : Path.GetFullPath(assetsDirectory);
        var found = root.Length > 0 && Directory.Exists(root) ? ReadDefinitions(root) : [];

        var names = new Dictionary<Guid, string>();
        var nodes = new Dictionary<string, (AssemblyDefinition Definition, string Path)>(StringComparer.Ordinal);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (definition, path, id) in found)
        {
            var name = string.IsNullOrWhiteSpace(definition.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : definition.Name.Trim();
            var directory = Path.GetDirectoryName(path)!;

            if (string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
                throw Conflict($"Assembly definition {path} uses the default assembly name '{defaultName}'.");
            if (!nodes.TryAdd(name, (definition, path)))
                throw Conflict($"Assembly name '{name}' is defined by both {nodes[name].Path} and {path}.");
            if (!directories.TryAdd(directory, path))
                throw Conflict($"Folder {directory} holds two assembly definitions: {directories[directory]} and {path}.");

            names[id] = name;
        }

        var assemblies = nodes.ToDictionary(
            static pair => pair.Key,
            pair => ToAssembly(pair.Key, pair.Value.Definition, pair.Value.Path, names),
            StringComparer.Ordinal);

        foreach (var assembly in assemblies.Values)
        {
            foreach (var reference in assembly.References)
            {
                if (!assembly.EditorOnly && assemblies[reference].EditorOnly)
                    throw Conflict($"Assembly '{assembly.Name}' ships in games but references editor-only '{reference}'.");
            }
        }

        var ordered = TopologicalOrder(assemblies);
        var defaultAssembly = new ProjectAssembly(
            defaultName,
            defaultName,
            root,
            null,
            [.. ordered.Where(a => !a.EditorOnly && nodes[a.Name].Definition.AutoReferenced).Select(static a => a.Name)],
            EditorOnly: false,
            AllowUnsafeCode: false);

        return new AssemblyGraph(defaultAssembly, ordered);
    }

    /// <summary>Whether a data-asset file holds an <see cref="AssemblyDefinition"/>, read without loading it.</summary>
    /// <param name="path">The data-asset file.</param>
    /// <returns>True for an assembly definition.</returns>
    public static bool IsDefinitionFile(string path)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(path));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals(ObjectJsonSerializer<DataAsset>.TypeIdProperty))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                return reader.Read() && reader.TryGetGuid(out var id) && id == DefinitionTypeId;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogDebug(ex, "{Path} is not a readable data asset", path);
        }

        return false;
    }

    static List<(AssemblyDefinition Definition, string Path, Guid Id)> ReadDefinitions(string root)
    {
        var found = new List<(AssemblyDefinition, string, Guid)>();

        foreach (var path in ProjectSettingsLoader.DataAssetSources(root).Where(IsDefinitionFile)
                     .Select(Path.GetFullPath).Order(StringComparer.Ordinal))
        {
            if (DataAsset.LoadContent(path) is not AssemblyDefinition definition)
                throw Conflict($"Assembly definition {path} could not be read.");

            var id = Asset.Load($"{path}.meta")?.Id ?? definition.Id;
            found.Add((definition, path, id));
        }

        return found;
    }

    static ProjectAssembly ToAssembly(
        string name,
        AssemblyDefinition definition,
        string path,
        Dictionary<Guid, string> names)
    {
        var references = new List<string>();
        foreach (var reference in definition.References.Where(static r => !r.IsEmpty))
        {
            if (!names.TryGetValue(reference.AssetId, out var referenced))
                throw Conflict($"Assembly '{name}' references {reference.AssetId}, which is not an assembly definition.");
            if (referenced == name)
                throw Conflict($"Assembly '{name}' references itself.");
            if (!references.Contains(referenced)) references.Add(referenced);
        }

        return new ProjectAssembly(
            name,
            string.IsNullOrWhiteSpace(definition.RootNamespace) ? name : definition.RootNamespace.Trim(),
            Path.GetDirectoryName(path)!,
            path,
            references,
            definition.EditorOnly,
            definition.AllowUnsafeCode);
    }

    static List<ProjectAssembly> TopologicalOrder(Dictionary<string, ProjectAssembly> assemblies)
    {
        var ordered = new List<ProjectAssembly>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);

        void Visit(ProjectAssembly assembly, List<string> path)
        {
            if (state.TryGetValue(assembly.Name, out var done))
            {
                if (done) return;
                throw Conflict($"Assembly references form a cycle: {string.Join(" → ", path.Append(assembly.Name))}.");
            }

            state[assembly.Name] = false;
            path.Add(assembly.Name);
            foreach (var reference in assembly.References) Visit(assemblies[reference], path);
            path.RemoveAt(path.Count - 1);
            state[assembly.Name] = true;
            ordered.Add(assembly);
        }

        foreach (var assembly in assemblies.Values.OrderBy(static a => a.Name, StringComparer.Ordinal))
            Visit(assembly, []);

        return ordered;
    }

    static bool IsUnder(string directory, string parent) =>
        directory.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    static InvalidOperationException Conflict(string message) => new(message);
}
