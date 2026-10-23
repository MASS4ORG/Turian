using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mass4.BrickRelease;

static partial class ConsumerSmoke
{
    /// <summary>
    /// Installs the archive into a fresh consumer, compiles it and restores it cold from the lock, running each CLI call
    /// in a container of <paramref name="image"/>.
    /// </summary>
    internal static Task RunAsync(string archive, string image, CancellationToken cancellationToken,
        Func<string[], CancellationToken, Task<string>>? docker = null)
    {
        if (!ImagePattern().IsMatch(image))
        {
            throw new InvalidDataException("Consumer tests require an immutable Turian CLI image.");
        }

        docker ??= static (arguments, token) => ToolProcess.RunAsync("docker", arguments, token);
        return RunAsync(archive, "/test", "/input/package.brick", async (directory, arguments, token) =>
        {
            var user = (await ToolProcess.RunAsync("id", ["-u"], token)).Trim();
            var group = (await ToolProcess.RunAsync("id", ["-g"], token)).Trim();
            return await docker([ "run", "--rm", "--user", $"{user}:{group}", "-e", "HOME=/test/home",
                "-e", "GAYA_BRICKS=/test/store", "-e", "GAYA_CONFIG_HOME=/test/config", "-v", $"{directory}:/test",
                "-v", $"{Path.GetFullPath(archive)}:/input/package.brick:ro", image, .. arguments ], token);
        }, cancellationToken);
    }

    /// <summary>
    /// The same checks with the <c>turian-cli</c> this process can start, as inside the CLI image, isolated by its own
    /// home, brick store and configuration.
    /// </summary>
    internal static Task RunLocalAsync(string archive, CancellationToken cancellationToken,
        Func<string[], IReadOnlyDictionary<string, string>, CancellationToken, Task<string>>? cli = null)
    {
        cli ??= static (arguments, environment, token) => ToolProcess.RunAsync("turian-cli", arguments, token, environment: environment);
        return RunAsync(archive, null, Path.GetFullPath(archive), (directory, arguments, token) => cli(arguments,
            new Dictionary<string, string>
            {
                ["HOME"] = Path.Combine(directory, "home"),
                ["GAYA_BRICKS"] = Path.Combine(directory, "store"),
                ["GAYA_CONFIG_HOME"] = Path.Combine(directory, "config"),
            }, token), cancellationToken);
    }

    /// <param name="archive">The packed brick.</param>
    /// <param name="root">The scratch folder as the CLI sees it; null when it sees the real one.</param>
    /// <param name="input">The archive as the CLI sees it.</param>
    /// <param name="run">Runs one CLI call against the scratch folder.</param>
    /// <param name="cancellationToken">Stops the calls.</param>
    static async Task RunAsync(string archive, string? root, string input,
        Func<string, string[], CancellationToken, Task<string>> run, CancellationToken cancellationToken)
    {
        var identity = ReleaseArchive.Read(archive);
        var studio = IsStudioOnly(archive);
        var directory = Directory.CreateTempSubdirectory("brick-consumer-");
        try
        {
            // The image's dotnet refuses a HOME that does not exist, and compiling restores NuGet packages under it.
            Directory.CreateDirectory(Path.Combine(directory.FullName, "home"));
            var consumer = $"{root ?? directory.FullName}/consumer";
            var scope = studio ? new[] { "--studio" } : ["--project", consumer];
            if (!studio)
            {
                _ = await run(directory.FullName, ["new", consumer], cancellationToken);
            }

            _ = await run(directory.FullName, ["brick", "add", identity.PackageId, $"file:{input}", .. scope], cancellationToken);
            if (!studio)
            {
                _ = await run(directory.FullName, ["compile", consumer], cancellationToken);
            }

            _ = await run(directory.FullName, ["brick", "list", .. scope], cancellationToken);
            Directory.Delete(Path.Combine(directory.FullName, "store"), recursive: true);
            _ = await run(directory.FullName, ["brick", "restore", "--locked", .. scope], cancellationToken);
            Console.WriteLine($"PASS: {identity.PackageId} {identity.Version} consumer install and cold locked restore");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static bool IsStudioOnly(string archive)
    {
        using var packed = ZipFile.OpenRead(archive);
        using var stream = packed.GetEntry("package.json")!.Open();
        using var manifest = JsonDocument.Parse(stream);
        return manifest.RootElement.TryGetProperty("scopes", out var scopes)
               && scopes.ValueKind == JsonValueKind.Array && scopes.GetArrayLength() == 1
               && scopes[0].GetString() == "studio";
    }

    [GeneratedRegex(@"\Aghcr\.io/mass4org/turian-cli@sha256:[0-9a-f]{64}\z")]
    private static partial Regex ImagePattern();
}
