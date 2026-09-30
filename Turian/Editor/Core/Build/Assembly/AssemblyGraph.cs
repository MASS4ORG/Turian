using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>One assembly of a project: the default one or an <see cref="AssemblyDefinition"/>.</summary>
/// <param name="Name">The assembly name.</param>
/// <param name="RootNamespace">The root namespace of its generated project.</param>
/// <param name="Directories">
/// The folders it owns: the definition's own, then those of its <see cref="AssemblyDefinitionReference"/>s; the
/// <c>Assets</c> folder for the default assembly.
/// </param>
/// <param name="DefinitionPath">The definition's source file; null for the default assembly.</param>
/// <param name="References">Names of the project assemblies it compiles against.</param>
/// <param name="EditorOnly">Compiled and loaded by the editor only.</param>
/// <param name="AllowUnsafeCode">Allows <c>unsafe</c> code.</param>
/// <param name="NoEngineReferences">Compiles against .NET alone, without the engine.</param>
public sealed record ProjectAssembly(
    string Name,
    string RootNamespace,
    IReadOnlyList<string> Directories,
    string? DefinitionPath,
    IReadOnlyList<string> References,
    bool EditorOnly,
    bool AllowUnsafeCode,
    bool NoEngineReferences)
{
    /// <summary>Whether this is the default assembly, which holds every script outside a definition.</summary>
    public bool IsDefault => DefinitionPath is null;
}

/// <summary>
/// The assemblies a project's scripts compile into, read from the <see cref="AssemblyDefinition"/> and
/// <see cref="AssemblyDefinitionReference"/> files under <c>Assets</c>. Nothing needs importing: a build reads them
/// straight from the sources.
/// </summary>
public sealed class AssemblyGraph
{
    static readonly Guid DefinitionTypeId = Guid.Parse(AssemblyDefinition.TypeIdValue);
    static readonly Guid ReferenceTypeId = Guid.Parse(AssemblyDefinitionReference.TypeIdValue);

    readonly Dictionary<string, ProjectAssembly> byDirectory;

    AssemblyGraph(ProjectAssembly defaultAssembly, IReadOnlyList<ProjectAssembly> definitions,
        IReadOnlyList<string> sourceRoots)
    {
        Default = defaultAssembly;
        Definitions = definitions;
        SourceRoots = sourceRoots;
        byDirectory = definitions.SelectMany(static d => d.Directories.Select(directory => (directory, d)))
            .ToDictionary(static pair => pair.directory, static pair => pair.d, StringComparer.Ordinal);
    }

    /// <summary>The assembly holding every script outside a definition.</summary>
    public ProjectAssembly Default { get; }

    /// <summary>The defined assemblies, each after the ones it references.</summary>
    public IReadOnlyList<ProjectAssembly> Definitions { get; }

    /// <summary>The folders scripts are read from: the project's <c>Assets</c>, then each package's root.</summary>
    public IReadOnlyList<string> SourceRoots { get; }

    /// <summary>Every assembly, each after the ones it references; the default one last.</summary>
    public IEnumerable<ProjectAssembly> All => Definitions.Append(Default);

    /// <summary>
    /// The assembly a script compiles into: the definition or definition reference in its closest folder, else
    /// the default.
    /// </summary>
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

    /// <summary>The folders under <paramref name="assembly"/>'s own that another assembly owns.</summary>
    /// <param name="assembly">The assembly whose sources are collected.</param>
    /// <returns>Absolute folder paths.</returns>
    public IEnumerable<string> ExcludedDirectories(ProjectAssembly assembly) =>
        byDirectory.Where(pair => pair.Value != assembly
                                  && assembly.Directories.Any(own => IsUnder(pair.Key, own)))
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal);

    /// <summary>Reads the definitions under a project's <c>Assets</c> folder and checks they form a valid graph.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="defaultName">The name of the default assembly.</param>
    /// <param name="packages">
    /// The project's resolved packages; their assembly definitions join the graph, and every script in a package
    /// must belong to one.
    /// </param>
    /// <returns>The project's assemblies.</returns>
    /// <exception cref="InvalidOperationException">The definitions conflict, reference unknown assemblies or form a cycle.</exception>
    public static AssemblyGraph Discover(string assetsDirectory, string defaultName,
        IReadOnlyList<ResolvedPackage>? packages = null)
    {
        ArgumentNullException.ThrowIfNull(defaultName);
        packages ??= [];

        var root = string.IsNullOrWhiteSpace(assetsDirectory) ? string.Empty : Path.GetFullPath(assetsDirectory);
        var (found, referenceFiles) = root.Length > 0 && Directory.Exists(root) ? ReadSources(root) : ([], []);
        var names = new Dictionary<Guid, string>();
        var precastNames = new HashSet<string>(StringComparer.Ordinal);
        var sourcePackages = new List<ResolvedPackage>();
        foreach (var package in packages)
        {
            var (definitions, references) = ReadSources(package.RootPath);
            // An editor-only package ships nothing, whatever its definitions say.
            if (package.Manifest.EditorOnly) definitions.ForEach(static d => d.Definition.EditorOnly = true);

            // A brick with a prebuilt payload is not compiled: its assemblies are referenced as they are. Its
            // definitions still count, so an assembly of the project can name one of them.
            if (BrickAssemblies.IsPrecast(package))
            {
                foreach (var (definition, path, id) in definitions)
                {
                    var name = AssemblyName(definition, path);
                    names[id] = name;
                    precastNames.Add(name);
                }

                continue;
            }

            sourcePackages.Add(package);
            found.AddRange(definitions);
            referenceFiles.AddRange(references);
        }

        var nodes = new Dictionary<string, (AssemblyDefinition Definition, string Path)>(StringComparer.Ordinal);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (definition, path, id) in found)
        {
            var name = AssemblyName(definition, path);

            if (string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
                throw Conflict($"Assembly definition {path} uses the default assembly name '{defaultName}'.");
            if (!nodes.TryAdd(name, (definition, path)))
                throw Conflict($"Assembly name '{name}' is defined by both {nodes[name].Path} and {path}.");
            ClaimDirectory(directories, path);

            names[id] = name;
        }

        var extraDirectories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (reference, path) in referenceFiles)
        {
            if (reference.Definition is not { IsEmpty: false } target || !names.TryGetValue(target.AssetId, out var name))
                throw Conflict($"Assembly definition reference {path} does not point to an assembly definition.");

            ClaimDirectory(directories, path);
            (extraDirectories.TryGetValue(name, out var list) ? list : extraDirectories[name] = []).Add(
                Path.GetDirectoryName(path)!);
        }

        var assemblies = nodes.ToDictionary(
            static pair => pair.Key,
            pair => ToAssembly(pair.Key, pair.Value.Definition, pair.Value.Path, names, precastNames,
                extraDirectories.GetValueOrDefault(pair.Key) ?? []),
            StringComparer.Ordinal);

        foreach (var assembly in assemblies.Values)
        {
            foreach (var reference in assembly.References.Select(r => assemblies[r]))
            {
                if (!assembly.EditorOnly && reference.EditorOnly)
                    throw Conflict($"Assembly '{assembly.Name}' ships in games but references editor-only '{reference.Name}'.");
                if (assembly.NoEngineReferences && !reference.NoEngineReferences)
                    throw Conflict($"Assembly '{assembly.Name}' has no engine references but references '{reference.Name}', which does.");
            }
        }

        var ordered = TopologicalOrder(assemblies);
        var defaultAssembly = new ProjectAssembly(
            defaultName,
            defaultName,
            root.Length > 0 ? [root] : [],
            null,
            [.. ordered.Where(a => !a.EditorOnly && nodes[a.Name].Definition.AutoReferenced).Select(static a => a.Name)],
            EditorOnly: false,
            AllowUnsafeCode: false,
            NoEngineReferences: false);

        var graph = new AssemblyGraph(defaultAssembly, ordered,
            [.. defaultAssembly.Directories, .. sourcePackages.Select(static p => Path.GetFullPath(p.RootPath))]);

        foreach (var package in sourcePackages)
        {
            if (graph.Scripts(package.RootPath).FirstOrDefault(script => graph.AssemblyFor(script).IsDefault) is { } stray)
                throw Conflict($"Script {stray} in package {package.Id} is not under an assembly definition.");
        }

        return graph;
    }

    /// <summary>The scripts under a folder, leaving out folders whose name ends in <c>~</c>, which are never compiled.</summary>
    /// <param name="directory">The folder.</param>
    /// <returns>Absolute script paths.</returns>
    public IEnumerable<string> Scripts(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsInTildeFolder(Path.GetRelativePath(directory, path)))
            : [];

    static bool IsInTildeFolder(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).SkipLast(1)
            .Any(static segment => segment.EndsWith('~'));

    /// <summary>
    /// Whether a data-asset file holds an <see cref="AssemblyDefinition"/> or an
    /// <see cref="AssemblyDefinitionReference"/>, read without loading it.
    /// </summary>
    /// <param name="path">The data-asset file.</param>
    /// <returns>True for a file that decides which assembly scripts compile into.</returns>
    public static bool IsDefinitionFile(string path) => PeekTypeId(path) is { } id
                                                        && (id == DefinitionTypeId || id == ReferenceTypeId);

    static Guid? PeekTypeId(string path)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(path));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals(ObjectJsonSerializer<DataAsset>.TypeIdProperty))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                return reader.Read() && reader.TryGetGuid(out var id) ? id : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogDebug(ex, "{Path} is not a readable data asset", path);
        }

        return null;
    }

    static (List<(AssemblyDefinition Definition, string Path, Guid Id)> Definitions,
        List<(AssemblyDefinitionReference Reference, string Path)> References) ReadSources(string root)
    {
        var definitions = new List<(AssemblyDefinition, string, Guid)>();
        var references = new List<(AssemblyDefinitionReference, string)>();

        foreach (var path in ProjectSettingsLoader.DataAssetSources(root).Select(Path.GetFullPath)
                     .Where(path => !IsInTildeFolder(Path.GetRelativePath(root, path)))
                     .Order(StringComparer.Ordinal))
        {
            var typeId = PeekTypeId(path);
            if (typeId != DefinitionTypeId && typeId != ReferenceTypeId) continue;

            switch (DataAsset.LoadContent(path))
            {
                case AssemblyDefinition definition:
                    definitions.Add((definition, path, Asset.Load($"{path}.meta")?.Id ?? definition.Id));
                    break;
                case AssemblyDefinitionReference reference:
                    references.Add((reference, path));
                    break;
                default:
                    throw Conflict($"{path} could not be read.");
            }
        }

        return (definitions, references);
    }

    static void ClaimDirectory(Dictionary<string, string> directories, string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!directories.TryAdd(directory, path))
            throw Conflict($"Folder {directory} holds two assembly definitions or references: {directories[directory]} and {path}.");
    }

    static string AssemblyName(AssemblyDefinition definition, string path) =>
        string.IsNullOrWhiteSpace(definition.Name) ? Path.GetFileNameWithoutExtension(path) : definition.Name.Trim();

    static ProjectAssembly ToAssembly(
        string name,
        AssemblyDefinition definition,
        string path,
        Dictionary<Guid, string> names,
        HashSet<string> precastNames,
        IEnumerable<string> extraDirectories)
    {
        var references = new List<string>();
        foreach (var reference in definition.References.Where(static r => r is { IsEmpty: false }))
        {
            if (!names.TryGetValue(reference.AssetId, out var referenced))
                throw Conflict($"Assembly '{name}' references {reference.AssetId}, which is not an assembly definition.");
            if (referenced == name)
                throw Conflict($"Assembly '{name}' references itself.");

            // A prebuilt assembly is referenced as a file by every project, not as a project of the graph.
            if (!precastNames.Contains(referenced) && !references.Contains(referenced)) references.Add(referenced);
        }

        return new ProjectAssembly(
            name,
            string.IsNullOrWhiteSpace(definition.RootNamespace) ? name : definition.RootNamespace.Trim(),
            [Path.GetDirectoryName(path)!, .. extraDirectories],
            path,
            references,
            definition.EditorOnly,
            definition.AllowUnsafeCode,
            definition.NoEngineReferences);
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
