using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gaya.Tests;

/// <summary>
/// Listing a registry's bricks with every version, and the presentation a store adds from its package pages, served
/// here by a local HTTP registry.
/// </summary>
public sealed class RegistryListingTests : IDisposable
{
    const string Index = """
        { "format": 1, "claims": {},
          "bricks": {
            "user.mateo.plain": { "versions": { "2.0.0": { "url": "p.brick", "integrity": "sha256-p" } } },
            "user.mateo.rules": { "versions": {
              "0.9.0": { "url": "a.brick", "integrity": "sha256-a" },
              "1.0.0": { "url": "b.brick", "integrity": "sha256-b",
                         "variants": { "source": { "url": "b.brick", "integrity": "sha256-b" }, "turian5.0": { "url": "c.brick", "integrity": "sha256-c" } } },
              "1.1.0": { "url": "d.brick", "integrity": "sha256-d", "yanked": true } } } } }
        """;

    readonly HttpListener listener = new();
    readonly string address;
    readonly CancellationTokenSource stop = new();
    readonly Task serving;
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-registry-listing-{Guid.NewGuid():N}");

    /// <summary>Serves a store on a free local port: its index, its catalog, and a page for one of its two bricks.</summary>
    public RegistryListingTests()
    {
        var port = FreePort();
        address = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(address);
        listener.Start();
        serving = Task.Run(Serve);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        stop.Cancel();
        listener.Close();
        _ = serving.Wait(TimeSpan.FromSeconds(5));
        stop.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Every version is listed newest first, a withdrawn one is never the latest, the published forms are kept, and a
    /// store's page supplies the newest version's manifest and archive size; a brick the store has no page for is
    /// still listed.
    /// </summary>
    [Fact]
    public async Task StoresAddPresentationToEveryListedVersion()
    {
        var registry = new ScopedRegistry { Name = "local", Url = $"{address}v1", AllowUnsigned = true };

        var catalog = await BrickRegistries.ListAsync([registry], TestContext.Current.CancellationToken);

        Assert.Empty(catalog.Problems);
        var rules = catalog.Bricks.Single(static b => b.Id == "user.mateo.rules");
        Assert.Equal(["1.1.0", "1.0.0", "0.9.0"], rules.Versions.Select(static v => v.Version));
        Assert.Equal("1.0.0", rules.Latest);
        Assert.True(rules.Versions[0].Yanked);
        Assert.Equal(["source", "turian5.0"], rules.Versions[1].Variants);
        Assert.Equal(1234, rules.Versions[1].Size);
        Assert.Contains("Rules Engine", rules.ManifestJson, StringComparison.Ordinal);
        var plain = catalog.Bricks.Single(static b => b.Id == "user.mateo.plain");
        Assert.Null(plain.ManifestJson);
        Assert.Equal("2.0.0", plain.Latest);
    }

    /// <summary>A registry that cannot be read is reported, while the others still list their bricks.</summary>
    [Fact]
    public async Task UnreadableRegistriesAreReported()
    {
        var missing = new ScopedRegistry { Name = "missing", Url = Path.Combine(root, "nowhere"), AllowUnsigned = true };
        var registry = new ScopedRegistry { Name = "local", Url = $"{address}v1", AllowUnsigned = true };

        var catalog = await BrickRegistries.ListAsync([missing, registry], TestContext.Current.CancellationToken);

        Assert.StartsWith("Registry missing:", Assert.Single(catalog.Problems), StringComparison.Ordinal);
        Assert.Equal(2, catalog.Bricks.Count);
    }

    /// <summary>One stored version is deleted with its recorded origin; a folder outside the store is refused.</summary>
    [Fact]
    public void StoresDeleteOneVersion()
    {
        var store = new PackageStore(Path.Combine(root, "store"));
        var folder = Path.Combine(store.Root, "user.mateo.rules@a");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), "{}");
        File.SetAttributes(Path.Combine(folder, "package.json"), FileAttributes.ReadOnly);
        store.RecordOrigin(folder, "registry:local");
        var kept = Path.Combine(store.Root, "user.mateo.rules@b");
        Directory.CreateDirectory(kept);

        Assert.True(store.Delete(folder));

        Assert.False(Directory.Exists(folder));
        Assert.Null(store.ReadOrigin(folder));
        Assert.True(Directory.Exists(kept));
        Assert.False(store.Delete(folder));
        Assert.Throws<PackageException>(() => store.Delete(root));
        Assert.Throws<PackageException>(() => store.Delete(Path.Combine(store.Root, ".origins")));
    }

    async Task Serve()
    {
        var rules = new PackageManifest { Name = "user.mateo.rules", Version = SemanticVersion.Parse("1.0.0"), DisplayName = "Rules Engine" };
        var page = new JsonObject
        {
            ["packageId"] = "user.mateo.rules",
            ["versions"] = new JsonArray(
                new JsonObject { ["version"] = "1.0.0", ["sizeBytes"] = 1234, ["manifestJson"] = rules.ToJson() },
                new JsonObject { ["version"] = "1.1.0", ["sizeBytes"] = 99, ["yanked"] = true }),
        }.ToJsonString();
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var (status, body) = context.Request.Url!.AbsolutePath switch
            {
                "/v1/index.json" => (200, Index),
                "/api/v1/packages" => (200, """{ "items": [], "total": 2 }"""),
                "/api/v1/packages/user.mateo.rules" => (200, page),
                _ => (404, ""),
            };
            context.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
