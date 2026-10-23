using MASS4.Bricks;
using NuGet.Versioning;

namespace Turian.Editor.Core;

/// <summary>The packages a brick's committed <c>Precast~/lib</c> copies came from.</summary>
/// <param name="Version">The package version the sync recorded.</param>
/// <param name="Files">The assembly file names copied into <c>Precast~/lib</c>.</param>
public sealed record SyncedPackage(string Version, IReadOnlyList<string> Files);

/// <summary>
/// Restores the NuGet packages of loaded bricks into the project's hermetic cache, so the editor resolves what a
/// brick's prebuilt assemblies reference. One generated stub project merges every loaded brick's <c>nuget</c> map,
/// which keeps a single lock file and one place for the version policy.
/// </summary>
public static class BrickNuGetRestore
{
    /// <summary>The folder, inside the project's <c>Bricks</c> directory, holding the merged stub project.</summary>
    public const string RestoreDirectoryName = "NuGet";

    /// <summary>The merged stub project restored for the loaded bricks.</summary>
    public const string StubProjectFileName = "BrickPackages.csproj";

    /// <summary>The lock file a restore writes beside the stub; a later restore runs locked against it.</summary>
    public const string LockFileName = "packages.lock.json";

    /// <summary>The record a <c>sync-precast</c> writes beside the copies, naming what came from which package.</summary>
    public const string SyncRecordFileName = "nuget-sync.json";

    /// <summary>The config that restricts the restore to given package sources.</summary>
    public const string NuGetConfigFileName = "NuGet.config";

    /// <summary>The folder, inside the project's <c>.Cache</c>, holding the restored packages.</summary>
    public const string CacheDirectoryName = "NuGet";

    /// <summary>The restored packages inside the cache directory.</summary>
    public const string PackagesDirectoryName = "packages";

    const string ProjectCacheDirectoryName = ".Cache";

    static readonly IReadOnlyDictionary<string, SyncedPackage> NoSyncedPackages =
        new Dictionary<string, SyncedPackage>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Restores the unified <c>nuget</c> maps of <paramref name="bricks"/> and returns the restored package folders.
    /// A map already covered by bricks' committed <c>Precast~</c> copies restores nothing, so a project whose bricks
    /// ship their copies loads offline.
    /// </summary>
    /// <param name="bricks">The loaded bricks.</param>
    /// <param name="projectRoot">The project folder, holding <c>Bricks</c> and <c>.Cache</c>.</param>
    /// <param name="logger">Receives restore failures.</param>
    /// <param name="platform">The build platform.</param>
    /// <param name="sources">Restrict the restore to these package sources, as for a private feed; the machine's own sources when none.</param>
    /// <returns>The restored package folders; empty when no brick maps a package or the copies already cover them.</returns>
    /// <exception cref="PackageException">The maps conflict, or the restore fails.</exception>
    public static IReadOnlyList<string> EnsureRestored(IEnumerable<ResolvedPackage> bricks, string projectRoot,
        ILogger logger, string platform = "desktop", IReadOnlyList<string>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(bricks);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyDictionary<string, string> unified;
        try
        {
            unified = BrickNuGet.UnifyVersions(bricks, platform);
        }
        catch (InvalidOperationException ex)
        {
            throw new PackageException(ex.Message, ex);
        }

        if (unified.Count == 0)
        {
            return [];
        }

        if (CoveredByPrecast(bricks, unified))
        {
            logger.LogInformation("Brick NuGet packages are already committed in Precast~, so nothing was restored");
            return [];
        }

        var stub = Path.Combine(projectRoot, ProjectManifest.DirectoryName, RestoreDirectoryName);
        var packages = Path.Combine(projectRoot, ProjectCacheDirectoryName, CacheDirectoryName, PackagesDirectoryName);
        Directory.CreateDirectory(stub);
        Directory.CreateDirectory(packages);
        WriteStub(stub, unified, sources);
        if (DropStaleLockFile(stub, unified))
        {
            logger.LogInformation("The bricks' lock file no longer matches their maps, so it was rewritten");
        }

        RunRestore(stub, packages, logger);
        return PackageDirectories(packages, unified);
    }

    /// <summary>
    /// The packages a brick's committed <c>Precast~/lib</c> copies came from, as <c>sync-precast</c> recorded them.
    /// </summary>
    /// <param name="brickRoot">The brick folder.</param>
    /// <returns>The synced packages by id; empty when the brick was never synced or the record is unreadable.</returns>
    public static IReadOnlyDictionary<string, SyncedPackage> SyncedPackages(string brickRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brickRoot);

        var synced = new Dictionary<string, SyncedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in BrickAssemblies.PrecastDirectories(brickRoot))
        {
            foreach (var (package, entry) in ReadSyncedPackages(payload))
            {
                synced.TryAdd(package, entry);
            }
        }

        return synced;
    }

    internal static IReadOnlyDictionary<string, SyncedPackage> ReadSyncedPackages(string payload)
    {
        var path = Path.Combine(payload, SyncRecordFileName);
        if (!File.Exists(path))
        {
            return NoSyncedPackages;
        }

        try
        {
            return ParseSyncedPackages(JsonNode.Parse(File.ReadAllText(path))?["packages"]?.AsObject());
        }
        catch (JsonException)
        {
            return NoSyncedPackages;
        }
    }

    static IReadOnlyDictionary<string, SyncedPackage> ParseSyncedPackages(JsonObject? packages)
    {
        var synced = new Dictionary<string, SyncedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, entry) in packages ?? new JsonObject())
        {
            if (ReadSyncedEntry(entry) is { } record)
            {
                synced[package] = record;
            }
        }
        return synced;
    }

    static SyncedPackage? ReadSyncedEntry(JsonNode? entry)
    {
        if (entry?.AsObject()["version"]?.GetValue<string>() is not { } version)
        {
            return null;
        }
        return new SyncedPackage(version, ReadSyncedFileNames(entry["files"]));
    }

    static IReadOnlyList<string> ReadSyncedFileNames(JsonNode? files) =>
        [.. files?.AsArray().Select(static file => file?.GetValue<string>()).OfType<string>() ?? []];

    /// <summary>
    /// Whether every unified package is already committed beside a brick's prebuilt assembly, which is what lets a
    /// fresh clone load its bricks without reaching a feed.
    /// </summary>
    /// <param name="bricks">The loaded bricks.</param>
    /// <param name="unified">Package id -> unified version.</param>
    /// <returns>True when one brick's copies cover every package at the unified version.</returns>
    static bool CoveredByPrecast(IEnumerable<ResolvedPackage> bricks, IReadOnlyDictionary<string, string> unified)
    {
        var records = bricks.SelectMany(static brick => BrickAssemblies.PrecastDirectories(brick)
            .Select(static payload => (Payload: payload, Synced: ReadSyncedPackages(payload)))).ToList();
        return unified.All(package => records.Any(record => record.Synced.TryGetValue(package.Key, out var synced)
            && SameVersion(synced.Version, package.Value)
            && synced.Files.All(file => File.Exists(Path.Combine(record.Payload,
                "lib", file)))));
    }

    static bool SameVersion(string left, string right)
    {
        try
        {
            return NuGetVersion.Parse(left) == NuGetVersion.Parse(right);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Writes the merged stub project for <paramref name="unified"/> into <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder holding the stub project.</param>
    /// <param name="unified">Package id -> unified version.</param>
    /// <param name="sources">Package sources to restrict the restore to; the machine's own sources when none.</param>
    public static void WriteStub(string directory, IReadOnlyDictionary<string, string> unified,
        IReadOnlyList<string>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(unified);

        var references = string.Join(Environment.NewLine,
            unified.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static p => $"    <PackageReference Include=\"{p.Key}\" Version=\"{p.Value}\" />"));
        File.WriteAllText(Path.Combine(directory, StubProjectFileName),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
            {{references}}
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n") + "\n");

        if (sources is { Count: > 0 })
        {
            WriteSources(directory, sources);
        }
    }

    static void WriteSources(string directory, IReadOnlyList<string> sources)
    {
        var entries = string.Join(Environment.NewLine,
            sources.Select(static (source, index) => $"    <add key=\"brick{index}\" value=\"{source}\" />"));
        File.WriteAllText(Path.Combine(directory, NuGetConfigFileName),
            $"""
            <configuration>
              <packageSources>
                <clear />
            {entries}
              </packageSources>
            </configuration>
            """.ReplaceLineEndings("\n") + "\n");
    }

    /// <summary>Runs <c>dotnet restore</c> on the stub, locked when its lock file exists.</summary>
    /// <param name="directory">The folder holding the stub project.</param>
    /// <param name="packages">The hermetic packages folder the restore writes to.</param>
    /// <param name="logger">Receives the restore's outcome.</param>
    /// <exception cref="PackageException">The restore fails; the message carries the tool's output.</exception>
    public static void RunRestore(string directory, string packages, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var locked = File.Exists(Path.Combine(directory, LockFileName));
        var arguments = $"restore \"{Path.Combine(directory, StubProjectFileName)}\"" + (locked ? " --locked-mode" : string.Empty);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(DotnetLocator.Find(), arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        // A project-local packages folder keeps restores hermetic instead of sharing ~/.nuget/packages.
        process.StartInfo.Environment["NUGET_PACKAGES"] = Path.GetFullPath(packages);
        logger.LogInformation("Restoring brick NuGet packages ({Mode} mode)", locked ? "locked" : "floating");
        if (!process.Start())
        {
            throw new PackageException("Could not start dotnet restore for the bricks' NuGet packages.");
        }

        // Both pipes drain at once: a restore verbose enough to fill the error buffer would deadlock a reader that
        // waits on the output stream first.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new PackageException("Restoring the bricks' NuGet packages timed out after 10 minutes.");
        }

        if (process.ExitCode != 0)
        {
            var detail = string.Join(Environment.NewLine,
                new[] { output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult() }
                    .Where(static s => !string.IsNullOrWhiteSpace(s)));
            logger.LogError("Brick NuGet restore failed:{NewLine}{Detail}", Environment.NewLine, detail);
            throw new PackageException($"Restoring the bricks' NuGet packages failed:{Environment.NewLine}{detail}");
        }

        logger.LogInformation("Restored the bricks' NuGet packages");
    }

    /// <summary>
    /// Deletes the lock file when it no longer describes <paramref name="unified"/>. A locked restore reports a
    /// changed map as NU1004 and nothing rewrites the file, so without this a map change would leave the project's
    /// bricks unrestorable.
    /// </summary>
    /// <param name="directory">The folder holding the stub project.</param>
    /// <param name="unified">Package id -> unified version.</param>
    /// <returns>True when a stale lock file was deleted.</returns>
    public static bool DropStaleLockFile(string directory, IReadOnlyDictionary<string, string> unified)
    {
        ArgumentNullException.ThrowIfNull(unified);

        var path = Path.Combine(directory, LockFileName);
        if (!File.Exists(path) || DescribesMap(path, unified))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // Whether the lock file's direct packages are exactly the unified map; transitive entries come from those.
    static bool DescribesMap(string lockFile, IReadOnlyDictionary<string, string> unified)
    {
        JsonNode? locked;
        try
        {
            locked = JsonNode.Parse(File.ReadAllText(lockFile))?["dependencies"];
        }
        catch (JsonException)
        {
            return false;
        }

        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in locked?.AsObject() ?? [])
        {
            foreach (var package in framework.Value?.AsObject() ?? [])
            {
                var entry = package.Value?.AsObject();
                if (entry?["type"]?.GetValue<string>() is not ("Direct" or "Project"))
                {
                    continue;
                }

                pinned[package.Key] = entry["resolved"]?.GetValue<string>()
                    ?? (entry["requested"]?.GetValue<string>() ?? string.Empty).Trim('[', ']');
            }
        }

        return pinned.Count == unified.Count && unified.All(package =>
            pinned.TryGetValue(package.Key, out var version) && SameVersion(version, package.Value));
    }

    /// <summary>The machine's shared packages folder: <c>NUGET_PACKAGES</c>, else <c>~/.nuget/packages</c>.</summary>
    /// <returns>The folder restores write to without further configuration.</returns>
    internal static string DefaultPackagesFolder() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>The restored folders of <paramref name="unified"/> under <paramref name="packagesRoot"/>.</summary>
    /// <param name="packagesRoot">The hermetic packages folder.</param>
    /// <param name="unified">Package id -> unified version.</param>
    /// <returns>The folders that exist; a package the restore did not write is left out.</returns>
    public static IReadOnlyList<string> PackageDirectories(string packagesRoot, IReadOnlyDictionary<string, string> unified)
    {
        ArgumentNullException.ThrowIfNull(unified);

        return [.. unified.Select(p => Path.Combine(packagesRoot, p.Key.ToLowerInvariant(), p.Value))
            .Where(Directory.Exists).Order(StringComparer.Ordinal)];
    }
}
