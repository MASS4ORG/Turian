using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// What a Turian project can do with its bricks, in one place for the command line and the Studio panel.
/// </summary>
public static class BrickService
{
    /// <summary>The bricks a project resolves to, dependencies first.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The resolved bricks.</returns>
    public static IReadOnlyList<ResolvedPackage> List(string projectRoot) => ProjectPackages.Resolve(projectRoot).Packages;

    /// <summary>Resolves the project's bricks, fetching what the store lacks and writing the lock.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="locked">Install exactly what the lock records and fail on drift, as CI does.</param>
    /// <returns>The resolution.</returns>
    public static PackageResolution Restore(string projectRoot, bool locked = false) =>
        ProjectPackages.Resolve(projectRoot, locked);

    /// <summary>Fetches newer commits of git bricks and rewrites the lock.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="ids">The bricks to update; all of them when empty.</param>
    /// <returns>The resolution.</returns>
    public static PackageResolution Update(string projectRoot, IReadOnlyCollection<string>? ids = null) =>
        ProjectPackages.Update(projectRoot, ids is { Count: > 0 } ? ids.ToHashSet(StringComparer.Ordinal) : null);

    /// <summary>Declares a brick in the project and resolves, so a bad source fails now.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="spec">The source; <c>builtin:&lt;id&gt;</c> when the brick ships with the engine and none is given.</param>
    /// <returns>The resolution.</returns>
    public static PackageResolution Add(string projectRoot, string id, string? spec = null)
    {
        var (before, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        var previous = before.Dependencies.GetValueOrDefault(id);
        ProjectBricks.Add(projectRoot, id, spec ?? $"builtin:{id}");
        ProjectPackages.Invalidate(projectRoot);
        try
        {
            return ProjectPackages.Resolve(projectRoot);
        }
        catch (PackageException)
        {
            // A declaration that does not resolve must not stay in the manifest.
            if (previous is null) ProjectBricks.Remove(projectRoot, id);
            else ProjectBricks.Add(projectRoot, id, previous);
            ProjectPackages.Invalidate(projectRoot);
            throw;
        }
    }

    /// <summary>Removes a brick from the project's manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>Whether the project declared it.</returns>
    public static bool Remove(string projectRoot, string id)
    {
        var removed = ProjectBricks.Remove(projectRoot, id);
        ProjectPackages.Invalidate(projectRoot);
        return removed;
    }

    /// <summary>Copies an installed brick into the project as a writable fork.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>The fork's folder.</returns>
    /// <exception cref="PackageException">The project does not install the brick.</exception>
    public static string Embed(string projectRoot, string id)
    {
        var brick = List(projectRoot).FirstOrDefault(p => p.Id == id)
                    ?? throw new PackageException($"The project does not install {id}.");
        var fork = ProjectBricks.Embed(brick, projectRoot, ["gaya", ProjectPackages.HostName]);
        ProjectPackages.Invalidate(projectRoot);
        return fork;
    }

    /// <summary>Copies assets of an installed brick into the project's <c>Assets</c> folder, detached from the brick.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="assets">The assets to copy, relative to the brick folder.</param>
    /// <param name="destination">The folder, relative to <c>Assets</c>, the copies go into.</param>
    /// <param name="remapReferences">Whether the project's own files that point at the originals point at the copies afterwards.</param>
    /// <returns>What was copied.</returns>
    /// <exception cref="PackageException">The project does not install the brick, or it lacks an asset.</exception>
    public static IReadOnlyList<CopiedAsset> CopyAssets(string projectRoot, string id, IEnumerable<string> assets,
        string destination, bool remapReferences = false)
    {
        var brick = List(projectRoot).FirstOrDefault(p => p.Id == id)
                    ?? throw new PackageException($"The project does not install {id}.");
        return BrickAssetCopy.Copy(projectRoot, brick, assets, destination, remapReferences);
    }

    /// <summary>Packs a brick folder into a <c>.brick</c> file after verifying it.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="outputDirectory">Where the file and its hash are written.</param>
    /// <param name="precast">What the folder's <c>Precast~</c> payload was built with, from <see cref="PrecastAsync"/>.</param>
    /// <returns>The packed file.</returns>
    /// <exception cref="PackageException">The brick fails verification.</exception>
    public static BrickPackResult Pack(string packageRoot, string outputDirectory, PackagePrecast? precast = null)
    {
        if (BrickVerifier.Verify(packageRoot) is { Count: > 0 } issues)
            throw new PackageException($"{packageRoot} cannot be packed:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", issues)}");

        return BrickArchive.Pack(packageRoot, outputDirectory, ["gaya", ProjectPackages.HostName], precast);
    }

    /// <summary>
    /// Compiles a brick's assembly definitions into the <c>Precast~</c> folder beside its sources, so consumers load
    /// the assemblies instead of compiling the code. The brick is built inside a scratch project that installs it
    /// together with the default built-in bricks; a brick needing more declares the sources in its own dependencies.
    /// </summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="logger">Receives the compiler's messages.</param>
    /// <returns>What the payload was built with, to record in the packed file.</returns>
    /// <exception cref="PackageException">The brick is invalid or does not compile.</exception>
    public static async Task<PackagePrecast> PrecastAsync(string packageRoot, ILogger logger)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        var manifest = PackageManifest.Load(packageRoot, ["gaya", ProjectPackages.HostName]);
        var payload = Path.Combine(packageRoot, BrickAssemblies.PrecastFolder);
        if (Directory.Exists(payload)) Directory.Delete(payload, recursive: true);

        var scratch = Path.Combine(Path.GetTempPath(), $"turian-precast-{Guid.NewGuid():N}");
        try
        {
            _ = await new ProjectBootstrapper().CreateAsync(scratch).ConfigureAwait(false)
                ?? throw new PackageException("The scratch project for the precast build could not be created.");
            ProjectBricks.Add(scratch, manifest.Name, $"file:{packageRoot}");

            var appSettings = SettingsService.Load(scratch) ?? throw new PackageException("The scratch project has no settings.");
            ProjectSettingsLoader.LoadFromSources(appSettings);
            var buildSettings = (BuildAppSettings)new BuildAppSettings().Load(appSettings);
            var output = Path.Combine(scratch, "out");
            Directory.CreateDirectory(output);

            string compiled;
            try
            {
                compiled = await new CompileUserCode(appSettings, logger, output, forceRecompile: true).ExecuteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new PackageException($"{manifest.Name} does not compile: {ex.Message}", ex);
            }

            var own = CsProjectGenerator.DiscoverAssemblies(buildSettings).Definitions
                .Where(a => a.DefinitionPath is { } path && Path.GetFullPath(path).StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToList();
            var result = new PackagePrecast { BuiltWith = { [ProjectPackages.HostName] = ProjectPackages.EngineVersion } };
            foreach (var assembly in own)
            {
                var folder = Path.Combine(payload, assembly.EditorOnly ? "editor" : "lib");
                Directory.CreateDirectory(folder);
                File.Copy(Path.Combine(output, $"{assembly.Name}.dll"), Path.Combine(folder, $"{assembly.Name}.dll"));
                (assembly.EditorOnly ? result.EditorAssemblies : result.Assemblies).Add(assembly.Name);
            }

            if (own.Count == 0) throw new PackageException($"{manifest.Name} has no assembly definition to precast.");

            var names = own.Select(static a => a.Name).ToHashSet(StringComparer.Ordinal);
            var types = new UserCodeTypeManifest();
            if (JsonSerializer.Deserialize<UserCodeTypeManifest>(File.ReadAllText(UserCodeTypeManifest.ManifestPathFor(compiled))) is { } built)
                types.Types.AddRange(built.Types.Where(t => t.Assembly is not null && names.Contains(t.Assembly)));
            File.WriteAllText(Path.Combine(payload, BrickAssemblies.TypesFile),
                JsonSerializer.Serialize(types, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
            return result;
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Creates a brick folder <c>&lt;parent&gt;/&lt;id&gt;</c> with a manifest, an assembly definition and a script.</summary>
    /// <param name="parent">The folder the brick folder is created in.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="displayName">The name shown to users; derived from the id when empty.</param>
    /// <param name="license">The SPDX license expression.</param>
    /// <returns>The brick folder.</returns>
    /// <exception cref="PackageException">The id is invalid or the folder exists.</exception>
    public static string New(string parent, string id, string? displayName = null, string license = "MIT")
    {
        if (!PackageId.IsValid(id)) throw new PackageException($"'{id}' is not a valid package id (lowercase reverse-DNS, e.g. user.you.inventory).");
        var root = Path.Combine(parent, id);
        if (Directory.Exists(root)) throw new PackageException($"{root} already exists.");

        var segments = id.Split('.').Skip(1).Select(Pascal).ToArray();
        var assembly = string.Join('.', segments);
        var runtime = Path.Combine(root, "Runtime");
        Directory.CreateDirectory(runtime);

        new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("0.1.0"),
            DisplayName = displayName ?? string.Join(' ', segments[^1]),
            License = license,
            Engines = { [ProjectPackages.HostName] = VersionRange.Parse($">={ProjectPackages.EngineVersion.Major}.{ProjectPackages.EngineVersion.Minor}") },
            Categories = [$"{ProjectPackages.HostName}:gameplay"],
        }.Save(root);

        var definition = Path.Combine(runtime, $"{assembly}.dataasset");
        var definitionId = Guid.NewGuid();
        Serializer.Save(definition, new AssemblyDefinition { Id = definitionId, Name = assembly, RootNamespace = assembly });
        Serializer.Save($"{definition}.meta", new DataAssetAsset { Id = definitionId, RelativePath = $"Runtime/{assembly}.dataasset" });

        var script = Path.Combine(runtime, $"{segments[^1]}Component.cs");
        File.WriteAllText(script, $$"""
            using Turian.Engine.Core;

            namespace {{assembly}};

            /// <summary>A starting point for the {{segments[^1]}} brick.</summary>
            public class {{segments[^1]}}Component : Component
            {
            }
            """.ReplaceLineEndings("\n") + "\n");
        Serializer.Save($"{script}.meta", new Asset { Id = Guid.NewGuid(), RelativePath = $"Runtime/{Path.GetFileName(script)}" });
        File.WriteAllText(Path.Combine(root, ".gitignore"), "# Built by `turian-cli brick pack --precast`.\nPrecast~/\n.bricks/\n");
        return root;
    }

    static string Pascal(string segment) =>
        string.Concat(segment.Split('-', '_').Where(static s => s.Length > 0).Select(static s => char.ToUpperInvariant(s[0]) + s[1..]));
}
