using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Gaya.Tests;

/// <summary>Materialization verifies store trust and archive bytes before exposing writable producer files.</summary>
public sealed class BrickMaterializerTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("brick-materialize-").FullName;
    readonly Routes handler = new();
    string Target => Path.Combine(root, "output");

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Public and token-protected stores preserve producer projects and verify the source variant.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreSourcesAreVerifiedAndWritable(bool authenticated)
    {
        var (registry, index) = Store();
        var entry = index.Bricks["com.example.demo"].Versions["1.0.0"];
        entry.Variants = new()
        {
            ["source"] = new RegistryVersion
            {
                Url = entry.Url,
                Integrity = entry.Integrity,
                Signature = entry.Signature,
                PackageId = "com.example.demo",
                Version = "1.0.0",
                Variant = "source",
            }
        };
        handler.Add("/v1/index.json", Encoding.UTF8.GetBytes(index.Serialize()));
        var folder = await Materializer().MaterializeAsync(Source(), Target, registry,
            authenticated ? "read-only-token" : "", TestContext.Current.CancellationToken);

        Assert.Equal(Target, folder);
        Assert.True(File.Exists(Path.Combine(folder, "Brick~/Source/Producer.csproj")));
        File.AppendAllText(Path.Combine(folder, "Brick~/Source/Producer.cs"), "\n// editable\n");
        Assert.All(handler.Tokens, token => Assert.Equal(authenticated ? "read-only-token" : null, token));
        Assert.DoesNotContain("read-only-token", string.Join("", Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(root, ".materialize-*"));
    }

    /// <summary>Unknown, withdrawn, unsigned, altered and mismatched releases cannot create a destination.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("yanked")]
    [InlineData("unsigned")]
    [InlineData("untrusted")]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("variant")]
    [InlineData("no-source")]
    public async Task BadStoreSourcesLeaveNoOutput(string failure)
    {
        var (registry, index) = Store();
        var entry = index.Bricks["com.example.demo"].Versions["1.0.0"];
        switch (failure)
        {
            case "missing": index.Bricks.Clear(); break;
            case "yanked": entry.Yanked = true; break;
            case "unsigned": entry.Signature = null; break;
            case "untrusted": registry.Keys.Clear(); break;
            case "hash": handler.Add("/v1/source.brick", [1, 2, 3]); break;
            case "identity": entry.PackageId = "com.other.demo"; break;
            case "variant": entry.Variant = "turian3.7"; break;
            case "no-source": entry.Variants = []; break;
        }

        handler.Add("/v1/index.json", Encoding.UTF8.GetBytes(index.Serialize()));
        await Assert.ThrowsAsync<PackageException>(() => Materializer().MaterializeAsync(Source(), Target, registry,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Target));
        Assert.Empty(Directory.GetDirectories(root, ".materialize-*"));
    }

    /// <summary>Checked ZIP, tar and tar.gz producer archives support flat and enclosing-folder layouts.</summary>
    [Theory]
    [InlineData("zip", false)]
    [InlineData("zip", true)]
    [InlineData("tar", false)]
    [InlineData("gzip", true)]
    public async Task ArchivesPreserveProducerLayouts(string format, bool nested)
    {
        var bytes = Archive(format, nested ? "repo/" : "");
        handler.Add("/source", bytes);
        var source = ArchiveSource(bytes);
        await Materializer().MaterializeAsync(source, Target, token: "read-token",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Target, "Brick~/Source/Producer.csproj")));
        Assert.Equal("read-token", Assert.Single(handler.Tokens));
    }

    /// <summary>Checksum mismatches and traversal or link entries never expose output files.</summary>
    [Theory]
    [InlineData("hash")]
    [InlineData("traversal")]
    [InlineData("symlink")]
    [InlineData("tar-link")]
    [InlineData("corrupt")]
    public async Task BadArchivesAreRejected(string failure)
    {
        var bytes = failure == "corrupt" ? new byte[3] : Archive("zip", "");
        if (failure is "traversal" or "symlink")
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry(failure == "traversal" ? "../escape.txt" : "link");
                if (failure == "symlink")
                {
                    entry.ExternalAttributes = 0xa000 << 16;
                }
            }

            bytes = stream.ToArray();
        }
        else if (failure == "tar-link")
        {
            using var stream = new MemoryStream();
            using (var writer = new TarWriter(stream, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../escape" });
            }

            bytes = stream.ToArray();
        }

        handler.Add("/source", bytes);
        var source = ArchiveSource(bytes) with { Sha256 = failure == "hash" ? new string('0', 64) : Hash(bytes) };
        await Assert.ThrowsAsync<PackageException>(() => Materializer().MaterializeAsync(source, Target,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Target));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        Assert.Empty(Directory.GetDirectories(root, ".materialize-*"));
    }

    /// <summary>Cross-origin redirects drop tokens; failed or insecure redirects report actionable failures.</summary>
    [Theory]
    [InlineData("cross-origin")]
    [InlineData("insecure")]
    [InlineData("missing")]
    [InlineData("loop")]
    [InlineData("refused")]
    public async Task ArchiveRedirectsDoNotLeakTokens(string route)
    {
        var bytes = Archive("zip", "");
        handler.Add("/download", bytes);
        handler.Redirect = route switch
        {
            "cross-origin" => "https://cdn.example/download",
            "insecure" => "http://cdn.example/download",
            "loop" => "https://store.example/source",
            _ => null,
        };
        handler.Status = route == "refused" ? HttpStatusCode.Unauthorized : HttpStatusCode.Redirect;
        var task = Materializer().MaterializeAsync(ArchiveSource(bytes), Target, token: "private-token",
            cancellationToken: TestContext.Current.CancellationToken);
        if (route == "cross-origin")
        {
            await task;
            Assert.Equal(new string?[] { "private-token", null }, handler.Tokens);
        }
        else
        {
            await Assert.ThrowsAsync<PackageException>(() => task);
            Assert.False(Directory.Exists(Target));
        }
    }

    /// <summary>Materialization refuses existing user files before issuing a network request.</summary>
    [Fact]
    public async Task ExistingFilesArePreserved()
    {
        Directory.CreateDirectory(Target);
        var keep = Path.Combine(Target, "keep.txt");
        File.WriteAllText(keep, "keep");
        await Assert.ThrowsAsync<PackageException>(() => Materializer().MaterializeAsync(Source(), Target,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("keep", File.ReadAllText(keep));
        Assert.Empty(handler.Tokens);
    }

    /// <summary>A source URL on a different scheme, port or host does not receive the store's read token.</summary>
    [Theory]
    [InlineData("https://store.example:444/source.brick")]
    [InlineData("http://store.example/source.brick")]
    [InlineData("https://cdn.example/source.brick")]
    public async Task StoreTokensStayAtTheirOrigin(string url)
    {
        var (registry, index) = Store();
        index.Bricks["com.example.demo"].Versions["1.0.0"].Url = url;
        handler.Add("/source.brick", Archive("zip", ""));
        handler.Add("/v1/index.json", Encoding.UTF8.GetBytes(index.Serialize()));
        await Materializer().MaterializeAsync(Source(), Target, registry, "origin-token", TestContext.Current.CancellationToken);
        Assert.Equal(new string?[] { "origin-token", null }, handler.Tokens);
    }

    /// <summary>Precreated empty output folders are accepted without retaining staging directories.</summary>
    [Fact]
    public async Task EmptyDestinationsAreAccepted()
    {
        var bytes = Archive("zip", "");
        handler.Add("/source", bytes);
        Directory.CreateDirectory(Target);
        await Materializer().MaterializeAsync(ArchiveSource(bytes), Target,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Target, "package.json")));
    }

    /// <summary>Even a correctly signed archive must describe the requested brick identity and version.</summary>
    [Fact]
    public async Task StoreArchiveIdentityIsChecked()
    {
        var (registry, index) = Store();
        var bytes = Archive("zip", "");
        using (var stream = new MemoryStream())
        {
            stream.Write(bytes);
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                zip.GetEntry("package.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("package.json").Open());
                writer.Write("""{"id":"com.other.brick","version":"1.0.0"}""");
            }

            bytes = stream.ToArray();
        }

        var signer = TestKeys.Create(root, "other");
        registry.Keys = [signer.PublicKey];
        var entry = index.Bricks["com.example.demo"].Versions["1.0.0"];
        entry.Integrity = $"sha256-{Convert.ToBase64String(SHA256.HashData(bytes))}";
        entry.Signature = signer.Sign(RegistrySignatures.RegistryNamespace,
            RegistrySignatures.Message("com.example.demo", "1.0.0", entry.Integrity));
        handler.Add("/v1/index.json", Encoding.UTF8.GetBytes(index.Serialize()));
        handler.Add("/v1/source.brick", bytes);
        var error = await Assert.ThrowsAsync<PackageException>(() => Materializer().MaterializeAsync(Source(), Target,
            registry, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("does not match the requested source identity", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Target));
    }

    /// <summary>Stores that refuse a read token report an actionable error without exposing that token.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task StoreReadTokenRefusalsAreExplained(HttpStatusCode status)
    {
        handler.RegistryStatus = status;
        var registry = new ScopedRegistry { Name = "store.example", Url = "https://store.example/v1" };
        var error = await Assert.ThrowsAsync<PackageException>(() => Materializer().MaterializeAsync(Source(), Target,
            registry, "fixture-invalid-token", TestContext.Current.CancellationToken));
        Assert.Contains("an access token is needed or the one given is not valid", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-invalid-token", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Target));
    }

    BrickMaterializer Materializer() => new(new HttpClient(handler));
    static BrickMaterializationSource Source() => BrickMaterializationSource.Parse("com.example.demo@1.0.0");
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static BrickMaterializationSource ArchiveSource(byte[] bytes) => new()
    {
        Type = "archive",
        Url = "https://store.example/source",
        Sha256 = Hash(bytes),
        Path = "demo",
    };

    (ScopedRegistry Registry, RegistryIndex Index) Store()
    {
        var signer = TestKeys.Create(root, "registry");
        var bytes = Archive("zip", "");
        var integrity = $"sha256-{Convert.ToBase64String(SHA256.HashData(bytes))}";
        var index = new RegistryIndex
        {
            Bricks = new()
            {
                ["com.example.demo"] = new RegistryBrick
                {
                    Versions = new()
                    {
                        ["1.0.0"] = new RegistryVersion
                        {
                            Url = "source.brick",
                            Integrity = integrity,
                            Signature = signer.Sign(RegistrySignatures.RegistryNamespace,
                        RegistrySignatures.Message("com.example.demo", "1.0.0", integrity)),
                        },
                    }
                }
            },
        };
        handler.Add("/v1/source.brick", bytes);
        return (new ScopedRegistry { Name = "store.example", Url = "https://store.example/v1", Keys = [signer.PublicKey] }, index);
    }

    static byte[] Archive(string format, string prefix)
    {
        var files = new Dictionary<string, string>
        {
            ["package.json"] = """{"id":"com.example.demo","version":"1.0.0"}""",
            ["Brick~/Source/Producer.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
            ["Brick~/Source/Producer.cs"] = "public static class Producer { public const int Value = 42; }",
        };
        using var result = new MemoryStream();
        if (format == "zip")
        {
            using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry(prefix + "Brick~/").LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                zip.CreateEntry(prefix + "Brick~/Source/").LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                foreach (var (name, text) in files)
                {
                    var entry = zip.CreateEntry(prefix + name);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(text);
                }
            }
        }
        else
        {
            using Stream tar = format == "gzip" ? new GZipStream(result, CompressionMode.Compress, leaveOpen: true) : result;
            using var writer = new TarWriter(tar, leaveOpen: true);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, prefix + "Brick~/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, prefix + "Brick~/Source/"));
            foreach (var (name, text) in files)
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, prefix + name) { DataStream = content });
            }
        }

        return result.ToArray();
    }

    sealed class Routes : HttpMessageHandler
    {
        readonly Dictionary<string, byte[]> routes = [];
        internal List<string?> Tokens { get; } = [];
        internal string? Redirect { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal HttpStatusCode RegistryStatus { get; set; } = HttpStatusCode.OK;
        internal void Add(string path, byte[] bytes) => routes[path] = bytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/v1/index.json" && RegistryStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(RegistryStatus));
            }

            if (request.RequestUri!.AbsolutePath == "/source" && Status != HttpStatusCode.OK)
            {
                var response = new HttpResponseMessage(Status);
                response.Headers.Location = Redirect is null ? null : new Uri(Redirect);
                return Task.FromResult(response);
            }

            return Task.FromResult(routes.TryGetValue(request.RequestUri.AbsolutePath, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
