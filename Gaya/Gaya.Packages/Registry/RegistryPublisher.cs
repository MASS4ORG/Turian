namespace Gaya.Packages;

/// <summary>Signs with a private key the holder keeps; the registry and publishers each bring their own.</summary>
public interface IBrickSigner
{
    /// <summary>The signer's public key as an OpenSSH public key line.</summary>
    string PublicKey { get; }

    /// <summary>Signs <paramref name="message"/> under <paramref name="namespace"/>.</summary>
    /// <param name="namespace">The signature namespace.</param>
    /// <param name="message">The bytes to sign.</param>
    /// <returns>The armored OpenSSH signature.</returns>
    string Sign(string @namespace, ReadOnlySpan<byte> message);
}

/// <summary>
/// Signs by running the installed <c>ssh-keygen</c> on a private key file, so the key never enters this process and the
/// signatures are the ones the standard tools make and check.
/// </summary>
/// <param name="privateKeyPath">The OpenSSH private key file (an ed25519 key).</param>
public sealed class SshKeygenSigner(string privateKeyPath) : IBrickSigner
{
    /// <inheritdoc/>
    public string PublicKey => Run(["-y", "-f", privateKeyPath], null).Trim();

    /// <inheritdoc/>
    public string Sign(string @namespace, ReadOnlySpan<byte> message)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"gaya-sign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "message");
            File.WriteAllBytes(file, message);
            _ = Run(["-Y", "sign", "-f", privateKeyPath, "-n", @namespace, file], folder);
            return File.ReadAllText($"{file}.sig");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    static string Run(string[] arguments, string? workingDirectory)
    {
        var start = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start) ?? throw new PackageException("ssh-keygen could not be started.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : throw new PackageException($"ssh-keygen failed: {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PackageException("ssh-keygen (OpenSSH) is not installed or not on the PATH; signing needs it.", ex);
        }
    }
}

/// <summary>What publishing added to a registry.</summary>
/// <param name="Id">The brick id.</param>
/// <param name="Version">The published version.</param>
/// <param name="Url">The brick file's path relative to the registry's <c>v1</c> folder.</param>
public sealed record RegistryPublishResult(string Id, string Version, string Url);

/// <summary>
/// Adds bricks to a static registry folder: the files, the signed index entries and the ownership claims. The folder is
/// what gets uploaded to the registry's host, so publishing needs no server.
/// </summary>
public static class RegistryPublisher
{
    /// <summary>The folder, inside the registry's root, that holds the index, the keys and the bricks.</summary>
    public const string ApiFolder = "v1";

    /// <summary>The registry location of a root folder, which a project's scoped registry can name.</summary>
    /// <param name="registryRoot">The registry's root folder.</param>
    /// <returns>The <c>v1</c> folder.</returns>
    public static string LocationOf(string registryRoot) => Path.Combine(Path.GetFullPath(registryRoot), ApiFolder);

    /// <summary>Publishes a <c>.brick</c> file into a registry folder.</summary>
    /// <param name="registryRoot">The registry's root folder; created when it does not exist.</param>
    /// <param name="brickFile">The <c>.brick</c> file.</param>
    /// <param name="registrySigner">The registry's signer; its signature is what clients check.</param>
    /// <param name="publisherSigner">The publisher's signer, which proves who owns the brick's name prefix.</param>
    /// <param name="claim">A name prefix to claim for the publisher when nothing owns it yet; <c>user.&lt;name&gt;</c> is claimed without asking.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the brick's manifest may use without depending on them.</param>
    /// <returns>What was published.</returns>
    /// <exception cref="PackageException">The version exists, the name is owned by another key, or no key proves ownership.</exception>
    public static RegistryPublishResult Publish(string registryRoot, string brickFile, IBrickSigner registrySigner,
        IBrickSigner? publisherSigner, string? claim = null, IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
    {
        var manifest = BrickArchive.ReadManifest(brickFile, reservedCategoryPrefixes);
        var id = manifest.Name;
        var version = manifest.Version!.ToString();
        var location = LocationOf(registryRoot);
        var indexPath = Path.Combine(location, "index.json");
        var index = File.Exists(indexPath) ? RegistryIndex.Parse(File.ReadAllText(indexPath)) : new RegistryIndex();

        if (index.Bricks.TryGetValue(id, out var existing) && existing.Versions.ContainsKey(version))
            throw new PackageException($"{id} {version} is already published; versions are immutable, publish a new one.");

        var publisherKey = publisherSigner?.PublicKey;
        var publisherFingerprint = publisherKey is null ? null : SshSignature.Fingerprint(publisherKey);
        Authorize(index, id, claim, publisherFingerprint);

        var integrity = BrickArchive.ComputeIntegrity(brickFile);
        var message = RegistrySignatures.Message(id, version, integrity);
        var file = Path.GetFileName(brickFile);
        var relative = $"bricks/{id}/{version}/{file}";

        var target = Path.Combine(location, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(brickFile, target);
        if (File.Exists($"{brickFile}.sha256")) File.Copy($"{brickFile}.sha256", $"{target}.sha256");

        var registryKey = registrySigner.PublicKey;
        var entry = new RegistryVersion
        {
            Url = relative,
            Integrity = integrity,
            Signature = RegistrySignatures.Strip(registrySigner.Sign(RegistrySignatures.RegistryNamespace, message)),
            SignedBy = SshSignature.Fingerprint(registryKey),
            PublishedAt = DateTimeOffset.UtcNow,
            Dependencies = new Dictionary<string, string>(manifest.Dependencies),
            Engines = new Dictionary<string, VersionRange>(manifest.Engines),
            Store = manifest.Store,
        };
        if (publisherSigner is not null)
        {
            entry.PublisherSignature = RegistrySignatures.Strip(publisherSigner.Sign(RegistrySignatures.PublisherNamespace, message));
            entry.PublisherKey = publisherKey;
        }

        if (existing is null) index.Bricks[id] = existing = new RegistryBrick();
        existing.Versions[version] = entry;
        index.GeneratedAt = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(location);
        File.WriteAllText(indexPath, index.Serialize());
        WriteKeys(Path.Combine(location, "keys"), registryKey);
        return new RegistryPublishResult(id, version, relative);
    }

    /// <summary>Withdraws a version: it stays downloadable for projects that locked it and is never chosen again.</summary>
    /// <param name="registryRoot">The registry's root folder.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="version">The version.</param>
    /// <exception cref="PackageException">The registry does not hold that version.</exception>
    public static void Yank(string registryRoot, string id, string version)
    {
        var indexPath = Path.Combine(LocationOf(registryRoot), "index.json");
        var index = File.Exists(indexPath) ? RegistryIndex.Parse(File.ReadAllText(indexPath)) : new RegistryIndex();
        if (!index.Bricks.TryGetValue(id, out var brick) || !brick.Versions.TryGetValue(version, out var entry))
            throw new PackageException($"The registry has no {id} {version}.");

        entry.Yanked = true;
        index.GeneratedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(indexPath, index.Serialize());
    }

    static void Authorize(RegistryIndex index, string id, string? claim, string? publisherFingerprint)
    {
        var owner = index.Claims.Where(c => id == c.Key || id.StartsWith(c.Key + ".", StringComparison.Ordinal))
            .OrderByDescending(static c => c.Key.Length).ToList();

        if (owner.Count > 0)
        {
            var (ownedPrefix, ownedBy) = (owner[0].Key, owner[0].Value.Key);
            if (publisherFingerprint is null) throw new PackageException($"{ownedPrefix} is owned by a publisher; sign with their key (a publisher key is required).");
            if (ownedBy != publisherFingerprint)
                throw new PackageException($"{ownedPrefix} is owned by another publisher key ({ownedBy}); {publisherFingerprint} cannot publish {id}.");
            return;
        }

        var prefix = claim ?? Personal(id)
                     ?? throw new PackageException($"{id} is not owned by anyone yet; claim its prefix (for example {string.Join('.', id.Split('.').Take(2))}) with a publisher key.");
        if (id != prefix && !id.StartsWith(prefix + ".", StringComparison.Ordinal))
            throw new PackageException($"{id} is not under the prefix {prefix} being claimed.");
        if (publisherFingerprint is null) throw new PackageException($"Claiming {prefix} needs a publisher key, which owns it from then on.");

        index.Claims[prefix] = new RegistryClaim { Key = publisherFingerprint };
    }

    /// <summary>The personal prefix of a <c>user.&lt;name&gt;.…</c> id: first publish wins it.</summary>
    static string? Personal(string id)
    {
        var parts = id.Split('.');
        return parts is ["user", _, _, ..] ? $"user.{parts[1]}" : null;
    }

    static void WriteKeys(string path, string registryKey)
    {
        var keys = File.Exists(path) ? RegistryKeys.Parse(File.ReadAllText(path)) : new RegistryKeys();
        var fingerprint = SshSignature.Fingerprint(registryKey);
        if (keys.Keys.All(k => SshSignature.Fingerprint(k.PublicKey) != fingerprint))
            keys.Keys.Add(new RegistryKey { Id = fingerprint, PublicKey = registryKey });

        File.WriteAllText(path, JsonSerializer.Serialize(keys, PackageJson.Options));
    }
}
