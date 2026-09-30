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

    /// <summary>
    /// Packs a brick (after <see cref="Pack"/>'s checks) and publishes it into a static registry folder, signed by the
    /// registry's key and, for the names that have an owner, by the publisher's.
    /// </summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="registryRoot">The registry's root folder, which is what gets uploaded to its host.</param>
    /// <param name="registryKeyFile">The registry's private key file.</param>
    /// <param name="publisherKeyFile">The publisher's private key file, or null.</param>
    /// <param name="claim">A name prefix to claim for the publisher.</param>
    /// <param name="precast">What the brick's <c>Precast~</c> payload was built with, from <see cref="PrecastAsync"/>.</param>
    /// <returns>What was published.</returns>
    /// <exception cref="PackageException">The brick is invalid, the version exists, or the name belongs to another key.</exception>
    public static RegistryPublishResult Publish(string packageRoot, string registryRoot, string registryKeyFile,
        string? publisherKeyFile, string? claim = null, PackagePrecast? precast = null)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"turian-publish-{Guid.NewGuid():N}");
        try
        {
            var packed = Pack(packageRoot, scratch, precast);
            return RegistryPublisher.Publish(registryRoot, packed.Path, new SshKeygenSigner(registryKeyFile),
                publisherKeyFile is null ? null : new SshKeygenSigner(publisherKeyFile), claim, ["gaya", ProjectPackages.HostName]);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The registries a project takes bricks from: its own scoped ones, then the public one.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <returns>The registries.</returns>
    public static IReadOnlyList<ScopedRegistry> Registries(string projectRoot) =>
        [.. ProjectManifest.Load(projectRoot).Manifest.ScopedRegistries, ProjectPackages.PublicRegistry];

    /// <summary>Declares a registry in the project's manifest, replacing one of the same name.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="registry">The registry.</param>
    /// <exception cref="PackageException">The registry has no name, address or scope, or a key that is not an ed25519 public key.</exception>
    public static void AddRegistry(string projectRoot, ScopedRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (registry.Name.Length == 0 || registry.Url.Length == 0 || registry.Scopes.Count == 0)
            throw new PackageException("A registry needs a name, an address and at least one scope.");
        foreach (var key in registry.Keys) _ = SshSignature.ParsePublicKey(key);
        if (registry.Keys.Count == 0 && !registry.AllowUnsigned)
            throw new PackageException("A registry needs at least one trusted key (an OpenSSH public key), or must be marked to allow unsigned bricks.");

        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        manifest.ScopedRegistries.RemoveAll(existing => existing.Name == registry.Name);
        manifest.ScopedRegistries.Add(registry);
        manifest.Save(projectRoot);
        ProjectPackages.Invalidate(projectRoot);
    }

    /// <summary>Removes a registry from the project's manifest.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="name">The registry's name.</param>
    /// <returns>Whether the project declared it.</returns>
    public static bool RemoveRegistry(string projectRoot, string name)
    {
        var (manifest, _) = ProjectManifest.Load(projectRoot, includeUserOverride: false);
        if (manifest.ScopedRegistries.RemoveAll(existing => existing.Name == name) == 0) return false;

        manifest.Save(projectRoot);
        ProjectPackages.Invalidate(projectRoot);
        return true;
    }

    /// <summary>Finds bricks in the project's registries whose id contains <paramref name="query"/>.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="query">Text the id must contain; every brick when empty.</param>
    /// <returns>The registry, id and newest version of each match; a registry that cannot be read is reported in place of its bricks.</returns>
    public static async Task<IReadOnlyList<(string Registry, string Id, string Latest)>> SearchAsync(string projectRoot, string query)
    {
        var found = new List<(string, string, string)>();
        foreach (var registry in Registries(projectRoot))
        {
            try
            {
                var index = await new RegistryClient(registry).GetIndexAsync().ConfigureAwait(false);
                found.AddRange(index.Bricks.Where(b => b.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(b => (registry.Name, b.Key, Newest(b.Value))));
            }
            catch (PackageException ex)
            {
                found.Add((registry.Name, $"(unavailable: {ex.Message})", string.Empty));
            }
        }

        return found;
    }

    static string Newest(RegistryBrick brick) =>
        brick.Versions.Where(v => !v.Value.Yanked && SemanticVersion.TryParse(v.Key, out _)).Select(static v => SemanticVersion.Parse(v.Key))
            .OrderByDescending(static v => v).FirstOrDefault()?.ToString() ?? "(yanked)";

    /// <summary>What the fork of a brick in the project changed since it was copied.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>The files that differ from the version the fork was copied from.</returns>
    /// <exception cref="PackageException">The brick is not an embedded fork, or its original cannot be found.</exception>
    public static IReadOnlyList<ForkDifference> Diff(string projectRoot, string id) =>
        BrickFork.Diff(FetchUpstream(projectRoot, id).Folder, ForkFolder(projectRoot, id));

    /// <summary>
    /// Merges a new release of a fork's original into the fork, three ways. Without <paramref name="to"/> the original
    /// is fetched again from the source the manifest declares (a git brick moves to the newest commit of its ref); with
    /// it, from that source, which the manifest then declares.
    /// </summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="to">The source of the new release, or null for the declared one.</param>
    /// <returns>What happened to each file; conflicts are left in the fork as merge markers.</returns>
    /// <exception cref="PackageException">The brick is not an embedded fork, or an original cannot be found.</exception>
    public static RebaseResult Rebase(string projectRoot, string id, string? to = null)
    {
        var fork = ForkFolder(projectRoot, id);
        var previous = FetchUpstream(projectRoot, id);
        var packages = Path.Combine(projectRoot, ProjectManifest.DirectoryName);
        var spec = to ?? DeclaredSource(projectRoot, id);
        var next = ProjectPackages.Fetch(id, PackageSource.Parse(spec, packages));

        var result = BrickFork.Rebase(previous.Folder, next.Folder, fork);
        try
        {
            var manifest = PackageManifest.Load(fork, ["gaya", ProjectPackages.HostName]);
            var pin = next.Commit ?? next.Integrity;
            var version = PackageManifest.Load(next.Folder, ["gaya", ProjectPackages.HostName]).Version;
            manifest.Upstream = pin is null ? $"{id}@{version}" : $"{id}@{version} ({pin})";
            manifest.Save(fork);
        }
        catch (PackageException) when (result.Conflicts.Contains(PackageManifest.FileName))
        {
            // The manifest itself conflicted; its upstream note is set by hand once the conflict is resolved.
        }

        if (to is not null && !result.HasConflicts) ProjectBricks.Add(projectRoot, id, to);
        ProjectPackages.Invalidate(projectRoot);
        return result;
    }

    /// <summary>The folder of the original a fork was copied from, at the version it was copied from.</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="id">The brick id.</param>
    /// <returns>The original's folder.</returns>
    /// <exception cref="PackageException">The brick is not a fork, or its original cannot be found.</exception>
    public static FetchedPackage FetchUpstream(string projectRoot, string id)
    {
        var fork = ForkFolder(projectRoot, id);
        var upstream = PackageManifest.Load(fork, ["gaya", ProjectPackages.HostName]).Upstream
                       ?? throw new PackageException($"{id} was not made with embed: its package.json has no upstream note.");
        var open = upstream.IndexOf('(', StringComparison.Ordinal);
        var pin = open < 0 ? null : upstream[(open + 1)..].TrimEnd(')', ' ');

        var source = PackageSource.Parse(DeclaredSource(projectRoot, id), Path.Combine(projectRoot, ProjectManifest.DirectoryName));
        switch (source)
        {
            case GitSource git when pin is { Length: 40 }:
                return ProjectPackages.Fetch(id, git with { Ref = pin });
            case ArchiveSource when pin is not null && pin.StartsWith("sha256-", StringComparison.Ordinal):
                var hex = Convert.ToHexStringLower(Convert.FromBase64String(pin["sha256-".Length..]));
                var stored = new PackageStore(PackageStore.DefaultRoot()).PackagePath(id, $"sha256-{hex[..16]}");
                if (Directory.Exists(stored)) return new FetchedPackage(stored, null, pin);

                var current = ProjectPackages.Fetch(id, source);
                return current.Integrity == pin
                    ? current
                    : throw new PackageException($"The {id} file changed since the fork was made and the original is no longer in the store.");
            default:
                return ProjectPackages.Fetch(id, source);
        }
    }

    static string ForkFolder(string projectRoot, string id)
    {
        var fork = Path.Combine(projectRoot, ProjectManifest.DirectoryName, id);
        return File.Exists(Path.Combine(fork, PackageManifest.FileName))
            ? fork
            : throw new PackageException($"{id} is not embedded in the project; embed it first.");
    }

    static string DeclaredSource(string projectRoot, string id) =>
        ProjectManifest.Load(projectRoot, includeUserOverride: false).Manifest.Dependencies.GetValueOrDefault(id)
        ?? throw new PackageException($"The manifest declares no source for {id}; a fork needs one to be compared with its original.");

    /// <summary>Packs a brick folder into a <c>.brick</c> file after verifying it.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="outputDirectory">Where the file and its hash are written.</param>
    /// <param name="precast">What the folder's <c>Precast~</c> payload was built with, from <see cref="PrecastAsync"/>.</param>
    /// <returns>The packed file.</returns>
    /// <exception cref="PackageException">The brick fails verification.</exception>
    public static BrickPackResult Pack(string packageRoot, string outputDirectory, PackagePrecast? precast = null)
    {
        if (PackageManifest.Load(packageRoot, ["gaya", ProjectPackages.HostName]) is { Upstream: not null, Store.Redistribute: false } fork)
            throw new PackageException($"{fork.Name} is licensed so that it cannot be republished, which includes forks of it.");

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
