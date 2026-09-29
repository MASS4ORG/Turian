namespace Gaya.Packages;

/// <summary>
/// The machine-wide package store: one read-only folder per package id and git commit, shared by every project,
/// plus a mirror of each git repository fetched from. Git is run as the installed command-line tool, so sources use
/// the machine's own git credentials (SSH keys, credential helpers, tokens).
/// </summary>
/// <param name="root">The store folder.</param>
public sealed class PackageStore(string root)
{
    /// <summary>The store folder.</summary>
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>The default store folder: <paramref name="environmentVariable"/> when set, else <c>~/.{app}/packages</c>.</summary>
    /// <param name="applicationName">The host's folder name under the user's home.</param>
    /// <param name="environmentVariable">The variable that overrides the location, such as for a CI cache.</param>
    /// <returns>The store folder.</returns>
    public static string DefaultRoot(string applicationName, string environmentVariable) =>
        Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".{applicationName}", "packages");

    /// <summary>The store folder of a package at a git commit.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="commit">The full commit hash.</param>
    /// <returns>The folder, which may not exist yet.</returns>
    public string PackagePath(string id, string commit) => Path.Combine(Root, $"{id}@{commit}");

    /// <summary>Fetches a repository into its mirror and returns the commit <paramref name="source"/> points at.</summary>
    /// <param name="source">The repository and ref.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <returns>The full commit hash.</returns>
    /// <exception cref="PackageException">Git failed, or the ref does not exist.</exception>
    public async Task<string> ResolveCommitAsync(GitSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var mirror = await FetchMirrorAsync(source.Url, cancellationToken).ConfigureAwait(false);
        var output = await RunGitAsync(["--git-dir", mirror, "rev-parse", "--verify", $"{source.Ref ?? "HEAD"}^{{commit}}"],
            cancellationToken, $"{source}: ref '{source.Ref ?? "HEAD"}' not found").ConfigureAwait(false);
        return output.Trim();
    }

    /// <summary>
    /// The store folder holding the package at <paramref name="commit"/> of <paramref name="source"/>'s repository,
    /// extracted on first use. Store folders are never modified once written.
    /// </summary>
    /// <param name="source">The repository.</param>
    /// <param name="commit">The full commit hash.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <returns>The package folder.</returns>
    /// <exception cref="PackageException">Git failed or the commit holds no valid package.</exception>
    public async Task<string> CheckoutAsync(GitSource source, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var mirror = MirrorPath(source.Url);
        if (!Directory.Exists(mirror)) await FetchMirrorAsync(source.Url, cancellationToken).ConfigureAwait(false);

        var manifestJson = await RunGitAsync(["--git-dir", mirror, "show", $"{commit}:{PackageManifest.FileName}"],
            cancellationToken, $"{source}@{commit} has no {PackageManifest.FileName} at its root").ConfigureAwait(false);
        var id = JsonSerializer.Deserialize<PackageManifest>(manifestJson, PackageJson.Options)?.Name;
        if (!PackageId.IsValid(id)) throw new PackageException($"{source}@{commit}: '{id}' is not a valid package id.");

        var target = PackagePath(id, commit);
        if (Directory.Exists(target)) return target;

        var staging = Path.Combine(Root, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = new MemoryStream())
            {
                await RunGitAsync(["--git-dir", mirror, "archive", "--format=tar", commit], cancellationToken,
                    $"{source}@{commit} could not be archived", archive).ConfigureAwait(false);
                archive.Position = 0;
                await TarFile.ExtractToDirectoryAsync(archive, staging, overwriteFiles: false, cancellationToken)
                    .ConfigureAwait(false);
            }

            MakeReadOnly(staging);
            Directory.CreateDirectory(Root);
            Directory.Move(staging, target);
        }
        catch (IOException) when (Directory.Exists(target))
        {
            // Another process extracted the same commit first; its copy is identical.
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteReadOnly(staging);
        }

        return target;
    }

    /// <summary>
    /// A hash of a package folder's files and paths, <c>sha256-</c> + base64, the same on every platform.
    /// Version-control folders are left out.
    /// </summary>
    /// <param name="packageRoot">The package folder.</param>
    /// <returns>The integrity string.</returns>
    public static string ComputeIntegrity(string packageRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(file => (Relative: Path.GetRelativePath(packageRoot, file).Replace('\\', '/'), File: file))
            .Where(static f => !f.Relative.StartsWith(".git/", StringComparison.Ordinal) && f.Relative != ".git")
            .OrderBy(static f => f.Relative, StringComparer.Ordinal);

        foreach (var (relative, file) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return $"sha256-{Convert.ToBase64String(hash.GetHashAndReset())}";
    }

    async Task<string> FetchMirrorAsync(string url, CancellationToken cancellationToken)
    {
        var mirror = MirrorPath(url);
        if (Directory.Exists(mirror))
        {
            await RunGitAsync(["--git-dir", mirror, "fetch", "--prune", "--tags", "--force", "origin"], cancellationToken,
                $"git+{url}: fetch failed").ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            await RunGitAsync(["clone", "--mirror", "--quiet", url, mirror], cancellationToken,
                $"git+{url}: clone failed").ConfigureAwait(false);
        }

        return mirror;
    }

    string MirrorPath(string url) =>
        Path.Combine(Root, ".git-mirrors", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16]);

    static async Task<string> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        string failure, Stream? standardOutput = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        // Fail rather than wait on a credential prompt nobody can answer.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PackageException("git is not installed or not on the PATH; git package sources need it.", ex);
        }

        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        string output;
        if (standardOutput is null)
        {
            output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await process.StandardOutput.BaseStream.CopyToAsync(standardOutput, cancellationToken).ConfigureAwait(false);
            output = string.Empty;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new PackageException($"{failure}: {(await error.ConfigureAwait(false)).Trim()}");

        return output;
    }

    static void MakeReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
    }

    static void DeleteReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
