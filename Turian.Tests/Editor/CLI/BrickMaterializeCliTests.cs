using System.Net;
using System.Net.Sockets;

namespace Turian.Tests;

/// <summary>Store materialization produces independently compilable projects with fetch-only authentication.</summary>
[Trait("Category", "E2E")]
public sealed class BrickMaterializeCliTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-materialize-cli-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A custom store's signed producer compiles through a dependent project's ProjectReference.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomStoreProducerCompilesInSourceTests(bool authenticated)
    {
        var producer = Path.Combine(root, "producer");
        var source = Path.Combine(producer, "Brick~", "Source");
        Directory.CreateDirectory(source);
        new PackageManifest { Name = "com.example.producer", Version = SemanticVersion.Parse("1.0.0") }.Save(producer);
        File.WriteAllText(Path.Combine(source, "Producer.csproj"), Project());
        File.WriteAllText(Path.Combine(source, "Producer.cs"), "public static class Producer { public const int Value = 42; }");
        var packed = BrickArchive.Pack(producer, Path.Combine(root, "packed"));
        var key = Path.Combine(root, "test-key");
        var keygen = await Run("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key]);
        Assert.True(keygen.ExitCode == 0, keygen.Output);
        var signer = new SshKeygenSigner(key);
        var index = new RegistryIndex
        {
            Bricks = new()
            {
                ["com.example.producer"] = new RegistryBrick
                {
                    Versions = new()
                    {
                        ["1.0.0"] = new RegistryVersion
                        {
                            Url = "source.brick",
                            Integrity = packed.Integrity,
                            Signature = signer.Sign(RegistrySignatures.RegistryNamespace,
                        RegistrySignatures.Message("com.example.producer", "1.0.0", packed.Integrity)),
                        },
                    }
                }
            },
        };
        await using var store = new TestStore(new()
        {
            ["/v1/index.json"] = Encoding.UTF8.GetBytes(index.Serialize()),
            ["/v1/source.brick"] = File.ReadAllBytes(packed.Path),
        }, authenticated ? "Bearer fixture-read-token" : null);
        var destination = Path.Combine(root, "Bricks", "producer");
        var result = await Cli(["brick", "materialize", "com.example.producer@1.0.0", "--path", destination,
            "--store", store.Url, "--key", signer.PublicKey, "--variant", "source"],
            authenticated ? "fixture-read-token" : null);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.DoesNotContain("fixture-read-token", result.Output, StringComparison.Ordinal);
        Assert.Equal(2, store.Requests);

        var consumer = Path.Combine(root, "consumer");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"), Project(
            "<ItemGroup><ProjectReference Include=\"../Bricks/producer/Brick~/Source/Producer.csproj\" /></ItemGroup>"));
        File.WriteAllText(Path.Combine(consumer, "Consumer.cs"), "public static class Consumer { public const int Value = Producer.Value; }");
        var built = await Run("dotnet", ["build", Path.Combine(consumer, "Consumer.csproj"),
            "--nologo", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", $"-p:RestoreSources={consumer}"]);
        Assert.True(built.ExitCode == 0, built.Output);
        Assert.True(File.Exists(Path.Combine(consumer, "bin", "Debug", "net10.0", "Consumer.dll")));
    }

    /// <summary>Unpinned inputs fail with a CI annotation before writing a destination.</summary>
    [Fact]
    public async Task InvalidSourcesReportCiErrors()
    {
        var result = await Cli(["brick", "materialize", "com.example.demo@latest", "--root", root]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("::error::", result.Output, StringComparison.Ordinal);
        Assert.Contains("exact semantic version", result.Output, StringComparison.Ordinal);
    }

    static string Project(string items = "") =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{items}</Project>";

    Task<(int ExitCode, string Output)> Cli(string[] arguments, string? token = null) => Run("dotnet",
        [typeof(global::Turian.Editor.CLI.Program).Assembly.Location, .. arguments], token);

    async Task<(int ExitCode, string Output)> Run(string executable, string[] arguments, string? token = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[PackageStore.StoreVariable] = Path.Combine(root, "cache");
        start.Environment[GayaConfig.HomeVariable] = Path.Combine(root, "config");
        start.Environment.Remove("SOURCE_DEP_TOKEN");
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        if (token is not null)
        {
            start.Environment["SOURCE_DEP_TOKEN"] = token;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    sealed class TestStore : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stopping = new();
        readonly Task server;
        internal string Url { get; }
        internal int Requests { get; private set; }

        internal TestStore(Dictionary<string, byte[]> routes, string? authorization)
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            server = Task.Run(async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stopping.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var request = await reader.ReadLineAsync(stopping.Token);
                    string? received = null;
                    while (await reader.ReadLineAsync(stopping.Token) is { Length: > 0 } line)
                    {
                        if (line.StartsWith("Authorization: ", StringComparison.OrdinalIgnoreCase))
                        {
                            received = line["Authorization: ".Length..];
                        }
                    }

                    Requests++;
                    var authorized = received == authorization;
                    var found = routes.TryGetValue(request!.Split(' ')[1], out var body);
                    body = authorized && found ? body! : [];
                    var status = !authorized ? "401 Unauthorized" : found ? "200 OK" : "404 Not Found";
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, stopping.Token);
                    await stream.WriteAsync(body, stopping.Token);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            try
            {
                await server;
            }
            catch (OperationCanceledException)
            {
            }

            stopping.Dispose();
        }
    }
}
