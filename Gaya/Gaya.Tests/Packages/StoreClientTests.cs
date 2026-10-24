using System.Net;

namespace Gaya.Tests;

/// <summary>Adding a registry from its address alone, and publishing to and managing releases on an asset store.</summary>
public sealed class StoreClientTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIETjciFivC4Q7z6o7KhiydqNS1Z5D6+ZZFbGUxQgL7ur test";

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-store-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public StoreClientTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A store's address becomes its origin: https by default, and a registry path dropped.</summary>
    [Theory]
    [InlineData("store.mass4.org", "https://store.mass4.org/")]
    [InlineData("http://store.mass4.local:5080", "http://store.mass4.local:5080/")]
    [InlineData("http://store.mass4.local:5080/api/v1/registry/v1/", "http://store.mass4.local:5080/")]
    [InlineData("https://bricks.mass4.org/v1", "https://bricks.mass4.org/")]
    public void StoreAddressesBecomeOrigins(string address, string origin) =>
        Assert.Equal(origin, StoreClient.OriginOf(address).ToString());

    /// <summary>An address that is not http or https is refused.</summary>
    [Fact]
    public void NonHttpStoreAddressesAreRefused() =>
        Assert.Throws<PackageException>(static () => StoreClient.OriginOf("ftp://store.example"));

    /// <summary>A bare host is tried with https, then http, at the root, the store's <c>/v1</c>, then its older registry path.</summary>
    [Fact]
    public void BareHostsAreTriedOverHttpsThenHttp()
    {
        var candidates = RegistryDiscovery.Candidates("store.example").ToList();

        Assert.Equal(["https://store.example", "https://store.example/v1"], candidates[..2]);
        Assert.Contains("https://store.example/api/v1/registry/v1", candidates);
        Assert.Equal("http://store.example/api/v1/registry/v1", candidates[^1]);
        Assert.Equal([root], RegistryDiscovery.Candidates(root));
    }

    /// <summary>Scopes come from the owned prefixes, else the keys' scopes, else the bricks' leading segments.</summary>
    [Fact]
    public void ScopesAreReadFromTheRegistry()
    {
        var index = new RegistryIndex();
        index.Bricks["com.acme.rules"] = new RegistryBrick();
        index.Bricks["com.acme.shop"] = new RegistryBrick();
        var keys = new RegistryKeys();
        Assert.Equal(["com.acme"], RegistryDiscovery.ScopesOf(index, keys));

        keys.Keys.Add(new RegistryKey { PublicKey = Key, Scopes = ["com.studio"] });
        Assert.Equal(["com.studio"], RegistryDiscovery.ScopesOf(index, keys));

        index.Claims["org.mass4"] = new RegistryClaim();
        Assert.Equal(["org.mass4"], RegistryDiscovery.ScopesOf(index, keys));
        Assert.Empty(RegistryDiscovery.ScopesOf(new RegistryIndex(), new RegistryKeys()));
    }

    /// <summary>Only an address is needed; publisher claims and key scopes do not restrict the registry.</summary>
    [Fact]
    public async Task RegistriesAreCompletedFromTheirAddress()
    {
        var handler = new StoreHandler();
        handler.Routes["/api/v1/registry/v1/index.json"] = """{ "format": 1, "bricks": {}, "claims": { "org.mass4": { "key": "x" } } }""";
        handler.Routes["/api/v1/registry/v1/keys"] = $$"""{ "keys": [ { "id": "k", "publicKey": "{{Key}}", "scopes": ["com.acme"] } ] }""";

        var registry = await RegistryDiscovery.CompleteAsync(new ScopedRegistry { Url = "store.example:5080" },
            new HttpClient(handler), TestContext.Current.CancellationToken);

        Assert.Equal("store.example", registry.Name);
        Assert.Equal("https://store.example:5080/api/v1/registry/v1", registry.Url);
        Assert.Empty(registry.Scopes);
        Assert.True(registry.Serves("com.newpublisher.rules"));
        Assert.Equal([Key], registry.Keys);
        BrickRegistries.Validate(registry);
    }

    /// <summary>What the user typed is kept, and a complete registry needs no request.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("com.me")]
    public async Task TypedRegistryValuesAreKept(string? scope)
    {
        var complete = new ScopedRegistry
        {
            Name = "mine",
            Url = "https://nowhere.invalid",
            Scopes = scope is null ? [] : [scope],
            Keys = [Key],
        };
        var noRequests = new StoreHandler();
        Assert.Same(complete, await RegistryDiscovery.CompleteAsync(complete, new HttpClient(noRequests), TestContext.Current.CancellationToken));
        Assert.Empty(noRequests.Requests);

        var handler = new StoreHandler();
        handler.Routes["/v1/index.json"] = """{ "format": 1, "bricks": {}, "claims": {} }""";
        var empty = await RegistryDiscovery.CompleteAsync(new ScopedRegistry { Url = "https://store.example", AllowUnsigned = true },
            new HttpClient(handler), TestContext.Current.CancellationToken);
        Assert.Empty(empty.Scopes);
        BrickRegistries.Validate(empty);

        var scoped = await RegistryDiscovery.CompleteAsync(new ScopedRegistry { Url = "https://store.example", Scopes = ["com.me"], AllowUnsigned = true },
            new HttpClient(handler), TestContext.Current.CancellationToken);
        Assert.Equal(("store.example", "https://store.example/v1", true), (scoped.Name, scoped.Url, scoped.AllowUnsigned));
        Assert.Equal(["com.me"], scoped.Scopes);
        Assert.Empty(scoped.Keys);

        await Assert.ThrowsAsync<PackageException>(static () =>
            RegistryDiscovery.DiscoverAsync("https://nothing.example", new HttpClient(new StoreHandler()), TestContext.Current.CancellationToken));
    }

    /// <summary>Publishing uploads the archive with metadata signed for its id, version and hash, carrying the token.</summary>
    [Fact]
    public async Task PublishingUploadsSignedMetadata()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var handler = new StoreHandler();
        handler.Routes["/api/v1/publish"] = """{ "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "packageId": "org.mass4.demo", "version": "1.2.0", "approved": false }""";
        var store = new StoreClient("http://store.example:5080", "secret", new HttpClient(handler));

        var release = await store.PublishAsync(brick, new FakeSigner(), new FakeSigner(), sourceRevision: "abc123",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("org.mass4.demo", "1.2.0", false), (release.PackageId, release.Version, release.Approved));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("POST", "secret"), (request.Method, request.Token));
        var body = request.Body.Replace(" ", "", StringComparison.Ordinal);
        Assert.Contains("\"packageId\":\"org.mass4.demo\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"integrity\":\"{BrickArchive.ComputeIntegrity(brick)}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"signedBy\":\"SHA256:", body, StringComparison.Ordinal);
        Assert.Contains("\"sourceRevision\":\"abc123\"", body, StringComparison.Ordinal);
        Assert.Contains("name=archive", body, StringComparison.Ordinal);
    }

    /// <summary>Metadata signed elsewhere is uploaded as it is, once it is checked to describe the archive.</summary>
    [Fact]
    public async Task PresignedReleasesAreUploadedAsTheyAre()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var integrity = BrickArchive.ComputeIntegrity(brick);
        var releaseFile = Path.Combine(root, "release.json");
        File.WriteAllText(releaseFile, $$"""
            { "packageId": "org.mass4.demo", "version": "1.2.0", "integrity": "{{integrity}}",
              "signature": "sig", "signedBy": "SHA256:abc", "sourceRevision": "workstation-1" }
            """);
        var handler = new StoreHandler();
        handler.Routes["/api/v1/publish"] = """{ "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "packageId": "org.mass4.demo", "version": "1.2.0", "approved": true }""";
        var store = new StoreClient("store.example", "secret", new HttpClient(handler));

        var release = await store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(release.Approved);
        Assert.Equal(["GET", "GET", "POST"], handler.Requests.Select(static r => r.Method));
        var body = handler.Requests[^1].Body.Replace(" ", "", StringComparison.Ordinal);
        Assert.Contains("\"signature\":\"sig\"", body, StringComparison.Ordinal);
        Assert.Contains("\"sourceRevision\":\"workstation-1\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A re-run is read back by exact id, version and variant before upload: other bytes or another build image than
    /// the recorded one are refused without uploading, and identical bytes upload and bring back the store's warnings.
    /// </summary>
    [Fact]
    public async Task RerunsAreCheckedAgainstTheRecordedRelease()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var integrity = BrickArchive.ComputeIntegrity(brick);
        var releaseFile = SignedRelease(integrity, Image('b'));
        var handler = new StoreHandler();
        handler.Routes["/api/v1/releases/org.mass4.demo/1.2.0"] = Receipt("sha256-other", Image('a'));
        var store = new StoreClient("store.example", "secret", new HttpClient(handler));

        var bytes = await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("with other bytes", bytes.Message, StringComparison.Ordinal);
        Assert.Contains(handler.Uris, static u => u.Contains("variant=source", StringComparison.Ordinal));

        handler.Routes["/api/v1/releases/org.mass4.demo/1.2.0"] = Receipt(integrity, Image('a'));
        var image = await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains($"first built by {Image('a')}", image.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, static r => r.Method == "POST");

        handler.Routes["/api/v1/publish"] = Receipt(integrity, Image('a'), "\"sourceRevision differs\"");
        var rerun = await store.PublishSignedAsync(brick, SignedRelease(integrity, Image('a')), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["sourceRevision differs"], rerun.Warnings);
        Assert.Equal((Image('a'), "3.3.0"), (rerun.Build!.Image, rerun.Build.TurianVersion));
        Assert.Equal(("pending", PackageVariant.Source), (rerun.Status, rerun.Variant));
    }

    /// <summary>
    /// Curator advisories refuse a build before upload, linking the record, also when the upload omits its build and
    /// only the recorded one matches; warning-only advisories come back with the release.
    /// </summary>
    [Fact]
    public async Task AdvisoriesAreConsultedForSubmittedAndRecordedBuilds()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var integrity = BrickArchive.ComputeIntegrity(brick);
        var handler = new StoreHandler();
        handler.Routes["/api/v1/releases/org.mass4.demo/1.2.0"] = Receipt(integrity, Image('a'));
        handler.Routes["/api/v1/build-advisories"] = $$"""
            [ { "id": "11111111-1111-1111-1111-111111111111", "packagePrefix": "org.mass4", "image": "{{Image('a')}}", "message": "Bad packer.", "reject": true },
              { "id": "22222222-2222-2222-2222-222222222222", "packagePrefix": "com.other", "turianVersionRange": ">=0.0.0", "message": "Other.", "reject": true } ]
            """;
        var store = new StoreClient("store.example", "secret", new HttpClient(handler));

        var omitted = await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, SignedRelease(integrity, null), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("advisory 11111111-1111-1111-1111-111111111111: Bad packer. See https://store.example/api/v1/build-advisories/11111111-1111-1111-1111-111111111111", omitted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, static r => r.Method == "POST");

        handler.Routes.Remove("/api/v1/releases/org.mass4.demo/1.2.0");
        handler.Routes["/api/v1/build-advisories"] = """
            [ { "id": "33333333-3333-3333-3333-333333333333", "packagePrefix": "org.mass4.demo", "turianVersionRange": ">=3.0.0 <4.0.0", "message": "Rebake soon.", "reject": false } ]
            """;
        handler.Routes["/api/v1/publish"] = Receipt(integrity, Image('b'));
        var warned = await store.PublishSignedAsync(brick, SignedRelease(integrity, Image('b')), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Rebake soon.", Assert.Single(warned.Warnings!), StringComparison.Ordinal);
    }

    /// <summary>A partial build object is refused before any request, as the store would refuse it.</summary>
    [Fact]
    public async Task PartialBuildsAreRefused()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var releaseFile = Path.Combine(root, "partial.json");
        File.WriteAllText(releaseFile, $$"""
            { "packageId": "org.mass4.demo", "version": "1.2.0", "integrity": "{{BrickArchive.ComputeIntegrity(brick)}}",
              "signature": "sig", "signedBy": "SHA256:abc", "build": { "image": "{{Image('a')}}" } }
            """);
        var handler = new StoreHandler();

        var error = await Assert.ThrowsAsync<PackageException>(() => new StoreClient("store.example", "secret", new HttpClient(handler))
            .PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("image and turianVersion", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>An exact read the store answers with 404 is no release; advisories a store does not serve are none.</summary>
    [Fact]
    public async Task MissingReleasesAndAdvisoriesAreEmpty()
    {
        var store = new StoreClient("store.example", "secret", new HttpClient(new StoreHandler()));

        Assert.Null(await store.ReleaseAsync("org.mass4.demo", "1.2.0", "turian3.3", TestContext.Current.CancellationToken));
        Assert.Empty(await store.BuildAdvisoriesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A store that cannot be reached is reported as such rather than taken for an absent release.</summary>
    [Fact]
    public async Task UnreachableStoresAreReported()
    {
        var store = new StoreClient("store.example", "secret", new HttpClient(new UnreachableHandler()));

        var error = await Assert.ThrowsAsync<PackageException>(() => store.ReleaseAsync("org.mass4.demo", "1.2.0",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("could not be reached", error.Message, StringComparison.Ordinal);
    }

    sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    static string Image(char digit) => $"ghcr.io/example/cli@sha256:{new string(digit, 64)}";

    static string Receipt(string integrity, string image, string warnings = "") => $$"""
        { "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "packageId": "org.mass4.demo", "version": "1.2.0", "variant": "source",
          "integrity": "{{integrity}}", "status": "pending", "approved": false, "isPrivate": false, "sourceRevision": "first",
          "build": { "image": "{{image}}", "turianVersion": "3.3.0" }, "warnings": [{{warnings}}] }
        """;

    string SignedRelease(string integrity, string? image)
    {
        var path = Path.Combine(root, $"release-{Guid.NewGuid():N}.json");
        var build = image is null ? "" : $$""", "build": { "image": "{{image}}", "turianVersion": "3.3.0" }""";
        File.WriteAllText(path, $$"""
            { "packageId": "org.mass4.demo", "version": "1.2.0", "integrity": "{{integrity}}",
              "signature": "sig", "signedBy": "SHA256:abc", "sourceRevision": "second"{{build}} }
            """);
        return path;
    }

    /// <summary>Metadata for another archive, unsigned metadata or a file that is not JSON is refused before any upload.</summary>
    [Fact]
    public async Task MismatchedPresignedReleasesAreRefused()
    {
        var brick = Brick("org.mass4.demo", "1.2.0");
        var integrity = BrickArchive.ComputeIntegrity(brick);
        var handler = new StoreHandler();
        var store = new StoreClient("store.example", "secret", new HttpClient(handler));
        var releaseFile = Path.Combine(root, "release.json");

        File.WriteAllText(releaseFile, """{ "packageId": "org.mass4.demo", "version": "1.2.0", "integrity": "sha256-other", "signature": "s", "signedBy": "k" }""");
        var other = await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("not this archive", other.Message, StringComparison.Ordinal);

        File.WriteAllText(releaseFile, $$"""{ "packageId": "org.mass4.demo", "version": "1.2.0", "integrity": "{{integrity}}" }""");
        var unsigned = await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("no registry signature", unsigned.Message, StringComparison.Ordinal);

        File.WriteAllText(releaseFile, "{ not json");
        await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        File.WriteAllText(releaseFile, "[]");
        await Assert.ThrowsAsync<PackageException>(() => store.PublishSignedAsync(brick, releaseFile, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    /// <summary>A release's state is changed by its id and version in one request; an unknown version is explained.</summary>
    [Fact]
    public async Task ReleasesAreYankedByIdAndVersion()
    {
        var handler = new StoreHandler();
        handler.Routes["/api/v1/packages/org.mass4.demo/1.2.0/state"] = "";
        var store = new StoreClient("store.example", "secret", new HttpClient(handler));

        await store.SetStateAsync("org.mass4.demo", "1.2.0", new StoreReleaseState(Yanked: true), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(("PUT", "secret"), (request.Method, request.Token));
        Assert.Contains("\"yanked\":true", request.Body, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAsync<PackageException>(() =>
            store.SetStateAsync("org.mass4.demo", "9.9.9", new StoreReleaseState(DeprecationReason: "old"), TestContext.Current.CancellationToken));
        Assert.Contains("may not see it", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>Signing in sends the credentials without a bearer token and returns the token the store issued.</summary>
    [Fact]
    public async Task SigningInReturnsTheIssuedToken()
    {
        var handler = new StoreHandler();
        handler.Routes["/api/v1/auth/token"] = """{ "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "secret": "mass4_abc", "expiresUtc": "2026-11-16T00:00:00Z" }""";
        var store = new StoreClient("http://store.example:5080", "stale", new HttpClient(handler));

        var token = await store.LoginAsync("me@example.com", "pw", ["org.mass4"], "Gaya CLI", publish: false,
            days: 7, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("mass4_abc", token.Secret);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("POST", null), (request.Method, request.Token));
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(("me@example.com", "pw", 7), ((string?)body["email"], (string?)body["password"], (int?)body["days"]));
        Assert.Equal(["registry.read"], body["scopes"]!.AsArray().Select(static s => (string?)s));
        Assert.Equal(["org.mass4"], body["prefixes"]!.AsArray().Select(static s => (string?)s));
    }

    /// <summary>The token's account and prefixes are read with the token; an empty answer is not taken for an account.</summary>
    [Fact]
    public async Task AccountsAreDescribedForTheirToken()
    {
        var handler = new StoreHandler();
        handler.Routes["/api/v1/account"] = """{ "email": "me@example.com", "prefixes": [ "com.me", "org.mass4" ] }""";
        var account = await new StoreClient("store.example", "secret", new HttpClient(handler)).AccountAsync(TestContext.Current.CancellationToken);

        Assert.Equal("me@example.com", account.Email);
        Assert.Equal(["com.me", "org.mass4"], account.Prefixes);
        Assert.Equal(("GET", "secret"), (handler.Requests[0].Method, handler.Requests[0].Token));

        handler.Routes["/api/v1/account"] = "";
        await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient("store.example", "secret", new HttpClient(handler)).AccountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A refused sign-in reports the store's reason, and an empty answer is not taken for a token.</summary>
    [Fact]
    public async Task RefusedSignInsAreExplained()
    {
        var refused = new StoreHandler { Status = HttpStatusCode.Unauthorized };
        refused.Routes["/api/v1/auth/token"] = """{ "title": "Sign-in failed." }""";
        var failed = await Assert.ThrowsAsync<PackageException>(() => new StoreClient("store.example", http: new HttpClient(refused))
            .LoginAsync("me@example.com", "bad", ["org.mass4"], "Gaya CLI", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("Sign-in failed.", failed.Message, StringComparison.Ordinal);

        var empty = new StoreHandler();
        empty.Routes["/api/v1/auth/token"] = "";
        await Assert.ThrowsAsync<PackageException>(() => new StoreClient("store.example", http: new HttpClient(empty))
            .LoginAsync("me@example.com", "pw", ["org.mass4"], "Gaya CLI", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The store's own reason for a refusal is reported, and a missing token is explained before any request.</summary>
    [Fact]
    public async Task RefusalsAreExplained()
    {
        var handler = new StoreHandler { Status = HttpStatusCode.Forbidden };
        handler.Routes["/api/v1/manage/releases"] = """{ "title": "Publisher access required." }""";
        var refused = await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient("store.example", "secret", new HttpClient(handler)).ReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Publisher access required.", refused.Message, StringComparison.Ordinal);

        var unauthorized = new StoreHandler { Status = HttpStatusCode.Unauthorized };
        unauthorized.Routes["/api/v1/manage/releases"] = "";
        Assert.Contains("missing, expired or revoked", (await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient("store.example", "secret", new HttpClient(unauthorized)).ReleasesAsync(TestContext.Current.CancellationToken))).Message,
            StringComparison.Ordinal);

        var broken = new StoreHandler { Status = HttpStatusCode.BadGateway };
        broken.Routes["/api/v1/manage/releases"] = "<html>bad gateway</html>";
        Assert.Contains("502 BadGateway", (await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient("store.example", "secret", new HttpClient(broken)).ReleasesAsync(TestContext.Current.CancellationToken))).Message,
            StringComparison.Ordinal);

        var host = $"no-token-{Guid.NewGuid():N}.example";
        var missing = await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient(host, http: new HttpClient(handler)).ReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"brick login {host}'", missing.Message, StringComparison.Ordinal);
        var local = await Assert.ThrowsAsync<PackageException>(() =>
            new StoreClient($"http://{host}:5080", http: new HttpClient(handler)).ReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"brick login http://{host}:5080'", local.Message, StringComparison.Ordinal);
    }

    /// <summary>A saved token is kept beside the others in the credentials file, readable only by the user.</summary>
    [Fact]
    public void TokensAreSavedBesideOthers()
    {
        var path = Path.Combine(root, "credentials.json");
        RegistryClient.SaveToken("a.example", "one", path);
        RegistryClient.SaveToken("b.example", " two ", path);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(("one", "two"), ((string?)saved["a.example"], (string?)saved["b.example"]));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    /// <summary>A token is found by registry name, else by url; an unreadable or missing file gives none.</summary>
    [Fact]
    public void TokensAreReadByNameOrUrl()
    {
        var path = Path.Combine(root, "credentials.json");
        var name = $"store-{Guid.NewGuid():N}";
        Assert.Null(RegistryClient.TokenFor(new ScopedRegistry { Name = name }, path));

        RegistryClient.SaveToken(name, "by-name", path);
        RegistryClient.SaveToken("https://other.example/v1", "by-url", path);
        Assert.Equal("by-name", RegistryClient.TokenFor(new ScopedRegistry { Name = name, Url = "https://x.example" }, path));
        Assert.Equal("by-url", RegistryClient.TokenFor(new ScopedRegistry { Name = $"unknown-{name}", Url = "https://other.example/v1" }, path));

        File.WriteAllText(path, "{ not json");
        Assert.Null(RegistryClient.TokenFor(new ScopedRegistry { Name = name }, path));
        RegistryClient.SaveToken(name, "again", path);
        Assert.Equal("again", RegistryClient.TokenFor(new ScopedRegistry { Name = name }, path));
    }

    string Brick(string id, string version)
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
        new PackageManifest { Name = id, Version = SemanticVersion.Parse(version) }.Save(folder);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Content.txt"), id);
        return BrickArchive.Pack(folder, Path.Combine(root, "packed"), []).Path;
    }

    sealed class FakeSigner : IBrickSigner
    {
        public string PublicKey => Key;

        public string Sign(string @namespace, ReadOnlySpan<byte> message) =>
            $"-----BEGIN SSH SIGNATURE-----\n{Convert.ToBase64String(message)}\n-----END SSH SIGNATURE-----\n";
    }

    sealed class StoreHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Routes { get; } = [];

        public List<(string Method, string? Token, string Body)> Requests { get; } = [];

        public List<string> Uris { get; } = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.Headers.Authorization?.Parameter, body));
            Uris.Add(request.RequestUri!.PathAndQuery);
            return Routes.TryGetValue(request.RequestUri!.AbsolutePath, out var response)
                ? new HttpResponseMessage(Status) { Content = new StringContent(response) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
