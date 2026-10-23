namespace MASS4.Bricks;

/// <summary>A release a store holds, as its publisher sees it.</summary>
/// <param name="Id">The store's id for the release, which state changes name.</param>
/// <param name="PackageId">The brick id.</param>
/// <param name="Version">The version.</param>
/// <param name="Approved">Whether consumers can see it; a release waiting for approval is not in the index.</param>
/// <param name="Yanked">Withdrawn: kept for projects that locked it, never chosen again.</param>
/// <param name="Quarantined">Blocked by the store's maintainers.</param>
/// <param name="IsPrivate">Only readers with access to its owner's bricks can download it.</param>
/// <param name="Integrity">The archive's content hash.</param>
/// <param name="DeprecationReason">Why consumers should move off it; null when it is not deprecated.</param>
public sealed record StoreRelease(Guid Id, string PackageId, string Version, bool Approved, bool Yanked = false,
    bool Quarantined = false, bool IsPrivate = false, string? Integrity = null, string? DeprecationReason = null);

/// <summary>A change to a release's state; null members stay as they are.</summary>
/// <param name="Yanked">Withdraw the release, or put it back.</param>
/// <param name="DeprecationReason">Mark the release deprecated with this reason; empty clears it.</param>
public sealed record StoreReleaseState(bool? Yanked = null, string? DeprecationReason = null);

/// <summary>Who an access token acts for on a store.</summary>
/// <param name="Email">The account's email.</param>
/// <param name="Prefixes">The brick name prefixes the token may act on.</param>
public sealed record StoreAccount(string Email, IReadOnlyList<string> Prefixes);

/// <summary>An access token a store issued at sign-in.</summary>
/// <param name="Id">The store's id for the token, which its owner revokes it by.</param>
/// <param name="Secret">The bearer secret; the store keeps only its hash, so it is shown once.</param>
/// <param name="ExpiresUtc">When the store stops accepting it.</param>
public sealed record StoreToken(Guid Id, string Secret, DateTime ExpiresUtc);

/// <summary>
/// Publishes to an asset store and manages its releases over the store's HTTP API. Releases are immutable: a version is
/// never deleted, only yanked or deprecated. Requests carry the store's access token, which publishing requires.
/// </summary>
public sealed class StoreClient
{
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    readonly HttpClient http;
    readonly string? token;

    /// <summary>Creates a client for the store at <paramref name="address"/>.</summary>
    /// <param name="address">The store's host or URL; its registry address works too.</param>
    /// <param name="token">The access token, or null to look one up with <see cref="RegistryClient.TokenFor"/>.</param>
    /// <param name="http">The HTTP client to use; a shared one when null.</param>
    public StoreClient(string address, string? token = null, HttpClient? http = null)
    {
        Origin = OriginOf(address);
        this.http = http ?? SharedHttp;
        this.token = token ?? RegistryClient.TokenFor(new ScopedRegistry { Name = Origin.Host, Url = Origin.ToString() });
    }

    /// <summary>The store's origin, which its API paths are relative to.</summary>
    public Uri Origin { get; }

    /// <summary>
    /// The origin of a store from what a user types: https when no scheme is given, and the registry path of a
    /// registry address dropped.
    /// </summary>
    /// <param name="address">A host, an origin or a registry address.</param>
    /// <returns>The origin, ending in a slash.</returns>
    /// <exception cref="PackageException">The address is not an http or https location.</exception>
    public static Uri OriginOf(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var text = address.Trim();
        text = text.Contains("://", StringComparison.Ordinal) ? text : $"https://{text}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new PackageException($"{address} is not a store address.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in (string[])["/api/v1/registry/v1", "/v1"])
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path + "/").Uri;
    }

    /// <summary>
    /// Uploads a <c>.brick</c> file, signed by the registry's key and, for names a publisher key owns, by the publisher's.
    /// Publishing the same file again returns the existing release.
    /// </summary>
    /// <param name="brickFile">The packed brick.</param>
    /// <param name="registrySigner">The key the store trusts to sign releases.</param>
    /// <param name="publisherSigner">The key owning the brick's name prefix, or null when none is required.</param>
    /// <param name="isPrivate">Whether only readers with access to the publisher's bricks can download it.</param>
    /// <param name="sourceRevision">The source revision recorded with the release, such as a git commit.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The release; check <see cref="StoreRelease.Approved"/> before telling consumers it is available.</returns>
    /// <exception cref="PackageException">The store refused the release or cannot be reached.</exception>
    public async Task<StoreRelease> PublishAsync(string brickFile, IBrickSigner registrySigner, IBrickSigner? publisherSigner,
        bool isPrivate = false, string? sourceRevision = null, IReadOnlyCollection<string>? reservedCategoryPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrySigner);
        var manifest = BrickArchive.ReadManifest(brickFile, reservedCategoryPrefixes);
        var id = manifest.Name;
        var version = manifest.Version!.ToString();
        var integrity = BrickArchive.ComputeIntegrity(brickFile);
        var message = RegistrySignatures.Message(id, version, integrity);
        var release = new JsonObject
        {
            ["packageId"] = id,
            ["version"] = version,
            ["integrity"] = integrity,
            ["signature"] = RegistrySignatures.Strip(registrySigner.Sign(RegistrySignatures.RegistryNamespace, message)),
            ["signedBy"] = SshSignature.Fingerprint(registrySigner.PublicKey),
            ["publisherKey"] = publisherSigner?.PublicKey,
            ["publisherSignature"] = publisherSigner is null
                ? null
                : RegistrySignatures.Strip(publisherSigner.Sign(RegistrySignatures.PublisherNamespace, message)),
            ["sourceRevision"] = sourceRevision,
            ["isPrivate"] = isPrivate,
        };
        return await UploadAsync(brickFile, release, id, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a <c>.brick</c> file with release metadata signed elsewhere, such as on the publisher's workstation, so
    /// the private keys never reach this machine. The metadata is sent as it is, once its id, version and hash are
    /// checked against the archive.
    /// </summary>
    /// <param name="brickFile">The packed brick that was signed.</param>
    /// <param name="releaseFile">The signed release JSON: <c>packageId</c>, <c>version</c>, <c>integrity</c>, signatures and provenance.</param>
    /// <param name="reservedCategoryPrefixes">Category prefixes the manifest may use without depending on them.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The release; check <see cref="StoreRelease.Approved"/> before telling consumers it is available.</returns>
    /// <exception cref="PackageException">The metadata does not describe the archive, or the store refused the release.</exception>
    public async Task<StoreRelease> PublishSignedAsync(string brickFile, string releaseFile,
        IReadOnlyCollection<string>? reservedCategoryPrefixes = null, CancellationToken cancellationToken = default)
    {
        var manifest = BrickArchive.ReadManifest(brickFile, reservedCategoryPrefixes);
        var (id, version) = (manifest.Name, manifest.Version!.ToString());
        JsonObject release;
        try
        {
            release = JsonNode.Parse(await File.ReadAllTextAsync(releaseFile, cancellationToken).ConfigureAwait(false)) as JsonObject
                ?? throw new PackageException($"{releaseFile} is not a release object.");
        }
        catch (JsonException ex)
        {
            throw new PackageException($"{releaseFile} is not valid JSON: {ex.Message}", ex);
        }

        var expected = (id, version, BrickArchive.ComputeIntegrity(brickFile));
        var signedFor = ((string?)release["packageId"], (string?)release["version"], (string?)release["integrity"]);
        if (signedFor != expected)
        {
            throw new PackageException(
                $"{releaseFile} is signed for {signedFor.Item1} {signedFor.Item2} ({signedFor.Item3}), not this archive, {id} {version} ({expected.Item3}).");
        }

        if (release["signature"] is null || release["signedBy"] is null)
        {
            throw new PackageException($"{releaseFile} carries no registry signature; sign the release before uploading it.");
        }

        return await UploadAsync(brickFile, release, id, version, cancellationToken).ConfigureAwait(false);
    }

    async Task<StoreRelease> UploadAsync(string brickFile, JsonObject release, string id, string version,
        CancellationToken cancellationToken)
    {
        await using var archive = File.OpenRead(brickFile);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(release.ToJsonString(PackageJson.Options), Encoding.UTF8, "application/json"), "release" },
            { new StreamContent(archive), "archive", Path.GetFileName(brickFile) },
        };
        using var request = Request(HttpMethod.Post, "api/v1/publish", form);
        var published = await SendAsync<StoreRelease>(request, $"publish {id} {version}", cancellationToken).ConfigureAwait(false);
        return published ?? throw new PackageException($"{Origin.Host} did not describe the release it received.");
    }

    /// <summary>The releases the token's owner may manage.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The releases, by brick id and version.</returns>
    /// <exception cref="PackageException">The store refused the request or cannot be reached.</exception>
    public async Task<IReadOnlyList<StoreRelease>> ReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "api/v1/manage/releases", null);
        return await SendAsync<List<StoreRelease>>(request, "list releases", cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>The account the access token acts for, and the prefixes it may act on.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The account.</returns>
    /// <exception cref="PackageException">There is no token, the store refused it, or it cannot be reached.</exception>
    public async Task<StoreAccount> AccountAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "api/v1/account", null);
        return await SendAsync<StoreAccount>(request, "describe the account", cancellationToken).ConfigureAwait(false)
            ?? throw new PackageException($"{Origin.Host} did not describe the account.");
    }

    /// <summary>
    /// Exchanges an account's email and password for an access token, without the store's web pages. The token is
    /// limited to <paramref name="prefixes"/>, which the account must own.
    /// </summary>
    /// <param name="email">The account's email.</param>
    /// <param name="password">The account's password; production stores accept it only over https.</param>
    /// <param name="prefixes">The brick name prefixes the token may act on, such as <c>org.mass4</c>.</param>
    /// <param name="clientName">The label the store shows for the token, naming this machine or pipeline.</param>
    /// <param name="publish">Whether the token may publish and change releases, besides reading private bricks.</param>
    /// <param name="days">How long the token lasts, within the store's maximum.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The token.</returns>
    /// <exception cref="PackageException">The sign-in failed, or the store refused the prefixes or scopes.</exception>
    public async Task<StoreToken> LoginAsync(string email, string password, IReadOnlyCollection<string> prefixes,
        string clientName, bool publish = true, int days = 30, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(prefixes);
        var body = new JsonObject
        {
            ["email"] = email.Trim(),
            ["password"] = password,
            ["name"] = clientName,
            ["scopes"] = new JsonArray([.. (publish ? (string[])["registry.read", "registry.publish"] : ["registry.read"])
                .Select(static s => JsonValue.Create(s))]),
            ["prefixes"] = new JsonArray([.. prefixes.Select(static p => JsonValue.Create(p))]),
            ["days"] = days,
        };
        using var request = Request(HttpMethod.Post, "api/v1/auth/token",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), anonymous: true);
        var issued = await SendAsync<StoreToken>(request, "sign in", cancellationToken, anonymous: true).ConfigureAwait(false);
        return issued is { Secret.Length: > 0 } ? issued : throw new PackageException($"{Origin.Host} did not return a token.");
    }

    /// <summary>Changes the state of a release, found by its brick id and version.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="version">The version.</param>
    /// <param name="state">What to change.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="PackageException">The token's owner has no such release, or the store refused the change.</exception>
    public async Task SetStateAsync(string id, string version, StoreReleaseState state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(state);
        var body = new JsonObject { ["yanked"] = state.Yanked, ["deprecationReason"] = state.DeprecationReason };
        using var request = Request(HttpMethod.Put,
            $"api/v1/packages/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/state",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        _ = await SendAsync<JsonNode>(request, $"change {id} {version}", cancellationToken).ConfigureAwait(false);
    }

    HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content, bool anonymous = false)
    {
        var request = new HttpRequestMessage(method, new Uri(Origin, path)) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null && !anonymous)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    async Task<T?> SendAsync<T>(HttpRequestMessage request, string action, CancellationToken cancellationToken,
        bool anonymous = false)
    {
        if (token is null && !anonymous)
        {
            var address = Origin.Scheme == Uri.UriSchemeHttps ? Origin.Host : Origin.GetLeftPart(UriPartial.Authority);
            throw new PackageException(
                $"{Origin.Host} needs an access token to {action}: run 'brick login {address}', or set GAYA_REGISTRY_TOKEN_{Variable(Origin.Host)}.");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PackageException($"{Origin.Host} could not be reached to {action}: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new PackageException($"{Origin.Host} refused to {action}: {Reason(response.StatusCode, text)}");
            }

            return text.Length == 0 ? default : JsonSerializer.Deserialize<T>(text, Web);
        }
    }

    /// <summary>The store's own explanation, from its problem response, else the status.</summary>
    static string Reason(HttpStatusCode status, string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["title"]?.GetValue<string>() is { Length: > 0 } title)
            {
                return title;
            }
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "the access token is missing, expired or revoked.",
            HttpStatusCode.Forbidden =>
                "the access token does not allow this; publishing and changing releases need the registry.publish scope for the brick's prefix.",
            HttpStatusCode.NotFound => "it is not there, or the access token may not see it.",
            _ => $"{(int)status} {status}.",
        };
    }

    /// <summary>The environment variable suffix <see cref="RegistryClient.TokenFor"/> reads a registry's token from.</summary>
    internal static string Variable(string name) =>
        new([.. name.ToUpperInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
}
