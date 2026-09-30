namespace Gaya.Packages;

/// <summary>
/// Reads one registry: its index and the <c>.brick</c> files it serves. A registry is a static site, so this is plain
/// file fetching over HTTP, or from a folder for a mirror and for offline use.
/// </summary>
public sealed class RegistryClient
{
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(100) };

    readonly ScopedRegistry registry;
    readonly HttpClient http;
    readonly string? token;

    /// <summary>Creates a client for <paramref name="registry"/>.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="http">The HTTP client to use; a shared one when null.</param>
    /// <param name="token">The access token for entitled bricks, or null to look one up with <see cref="TokenFor"/>.</param>
    public RegistryClient(ScopedRegistry registry, HttpClient? http = null, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
        this.http = http ?? SharedHttp;
        this.token = token ?? TokenFor(registry);
    }

    /// <summary>The registry this client reads.</summary>
    public ScopedRegistry Registry => registry;

    /// <summary>
    /// The access token of a registry: the <c>GAYA_REGISTRY_TOKEN_&lt;NAME&gt;</c> variable (name upper-cased, other
    /// characters as <c>_</c>), else its entry in <c>~/.gaya/credentials.json</c>, which maps a registry name or url to a
    /// token. Tokens are never stored in a manifest or lock file.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <returns>The token, or null when there is none.</returns>
    public static string? TokenFor(ScopedRegistry registry)
    {
        var variable = "GAYA_REGISTRY_TOKEN_" + new string([.. registry.Name.ToUpperInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment) return fromEnvironment;

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gaya", "credentials.json");
        if (!File.Exists(path)) return null;

        try
        {
            var credentials = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            return (string?)credentials?[registry.Name] ?? (string?)credentials?[registry.Url];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the registry's <c>index.json</c>.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The index.</returns>
    /// <exception cref="PackageException">The registry cannot be reached or its index is invalid.</exception>
    public async Task<RegistryIndex> GetIndexAsync(CancellationToken cancellationToken = default) =>
        RegistryIndex.Parse(Encoding.UTF8.GetString(await ReadAsync("index.json", cancellationToken).ConfigureAwait(false)));

    /// <summary>Reads the registry's <c>keys</c> file.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The keys it publishes.</returns>
    /// <exception cref="PackageException">The registry cannot be reached or the file is invalid.</exception>
    public async Task<RegistryKeys> GetKeysAsync(CancellationToken cancellationToken = default) =>
        RegistryKeys.Parse(Encoding.UTF8.GetString(await ReadAsync("keys", cancellationToken).ConfigureAwait(false)));

    /// <summary>Downloads a version's <c>.brick</c> into <paramref name="directory"/>, checking it against the index's hash.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="version">The version.</param>
    /// <param name="entry">The version's index entry.</param>
    /// <param name="directory">The folder to write the file into.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The downloaded file.</returns>
    /// <exception cref="PackageException">The file cannot be fetched or does not match its hash.</exception>
    public async Task<string> DownloadAsync(string id, string version, RegistryVersion entry, string directory,
        CancellationToken cancellationToken = default)
    {
        if (entry.Store?.Entitlement is { Length: > 0 } && token is null)
            throw new PackageException(
                $"{id} {version} needs an access token from {registry.Name}: set GAYA_REGISTRY_TOKEN_{registry.Name.ToUpperInvariant()} or add it to ~/.gaya/credentials.json.");

        var bytes = await ReadAsync(entry.Url, cancellationToken).ConfigureAwait(false);
        var integrity = $"sha256-{Convert.ToBase64String(SHA256.HashData(bytes))}";
        if (integrity != entry.Integrity)
            throw new PackageException($"{id} {version} from {registry.Name} does not match the hash its index lists.");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{id}-{version}{BrickArchive.Extension}");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    async Task<byte[]> ReadAsync(string relative, CancellationToken cancellationToken)
    {
        var location = registry.Url;
        if (location.StartsWith("file:", StringComparison.Ordinal)) location = new Uri(location).LocalPath;

        if (!location.StartsWith("http://", StringComparison.Ordinal) && !location.StartsWith("https://", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(relative, location);
            return File.Exists(path)
                ? await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
                : throw new PackageException($"{registry.Name}: {path} does not exist.");
        }

        var baseUri = new Uri(location.EndsWith('/') ? location : location + "/");
        var uri = Uri.TryCreate(relative, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (token is not null && uri.Host == baseUri.Host) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PackageException($"{registry.Name} refused {uri}: an access token is needed or the one given is not valid.");
            _ = response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PackageException($"{registry.Name}: {uri} could not be fetched: {ex.Message}", ex);
        }
    }
}

/// <summary>Decides whether a registry's version entry is signed by a key the project trusts.</summary>
public static class RegistryTrust
{
    /// <summary>Checks an entry's signatures.</summary>
    /// <param name="registry">The registry, which holds the keys the project trusts for it.</param>
    /// <param name="id">The brick id.</param>
    /// <param name="version">The version.</param>
    /// <param name="entry">The version's index entry.</param>
    /// <returns>The fingerprint of the key that signed it; null for an unsigned entry a registry marked <c>AllowUnsigned</c> accepts.</returns>
    /// <exception cref="PackageException">The entry is unsigned, or no trusted key made its signature.</exception>
    public static string? Verify(ScopedRegistry registry, string id, string version, RegistryVersion entry)
    {
        var message = RegistrySignatures.Message(id, version, entry.Integrity);

        if (entry.PublisherSignature is { Length: > 0 } publisherSignature
            && (entry.PublisherKey is not { Length: > 0 } publisherKey
                || !SshSignature.Verify(publisherKey, RegistrySignatures.PublisherNamespace, message, publisherSignature)))
            throw new PackageException($"{id} {version}: the publisher's signature in {registry.Name} is not valid.");

        if (string.IsNullOrEmpty(entry.Signature))
            return registry.AllowUnsigned
                ? null
                : throw new PackageException($"{id} {version} is not signed, and {registry.Name} is not trusted to serve unsigned bricks.");

        foreach (var key in registry.Keys)
        {
            if (SshSignature.Verify(key, RegistrySignatures.RegistryNamespace, message, entry.Signature)) return SshSignature.Fingerprint(key);
        }

        throw new PackageException(
            $"{id} {version}: the signature in {registry.Name} was not made by any of the {registry.Keys.Count} key(s) this project trusts for it.");
    }
}
