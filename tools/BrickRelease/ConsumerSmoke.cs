using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mass4.BrickRelease;

static partial class ConsumerSmoke
{
    internal static async Task RunAsync(string archive, string image, CancellationToken cancellationToken,
        Func<string[], CancellationToken, Task<string>>? docker = null)
    {
        if (!ImagePattern().IsMatch(image))
        {
            throw new InvalidDataException("Consumer tests require an immutable Turian CLI image.");
        }

        var identity = ReleaseArchive.Read(archive);
        var studio = IsStudioOnly(archive);
        var directory = Directory.CreateTempSubdirectory("brick-consumer-");
        try
        {
            // The image's dotnet refuses a HOME that does not exist, and compiling restores NuGet packages under it.
            Directory.CreateDirectory(Path.Combine(directory.FullName, "home"));
            var user = (await ToolProcess.RunAsync("id", ["-u"], cancellationToken)).Trim();
            var group = (await ToolProcess.RunAsync("id", ["-g"], cancellationToken)).Trim();
            var cli = new[] { "run", "--rm", "--user", $"{user}:{group}", "-e", "HOME=/test/home",
                "-e", "GAYA_BRICKS=/test/store", "-e", "GAYA_CONFIG_HOME=/test/config", "-v",
                $"{directory.FullName}:/test", "-v", $"{Path.GetFullPath(archive)}:/input/package.brick:ro", image };
            docker ??= static (arguments, token) => ToolProcess.RunAsync("docker", arguments, token);
            var scope = studio ? new[] { "--studio" } : ["--project", "/test/consumer"];
            if (!studio)
            {
                _ = await docker([.. cli, "new", "/test/consumer"], cancellationToken);
            }

            _ = await docker([.. cli, "brick", "add", identity.PackageId, "file:/input/package.brick", .. scope],
                cancellationToken);
            if (!studio)
            {
                _ = await docker([.. cli, "compile", "/test/consumer"], cancellationToken);
            }

            _ = await docker([.. cli, "brick", "list", .. scope], cancellationToken);
            Directory.Delete(Path.Combine(directory.FullName, "store"), recursive: true);
            _ = await docker([.. cli, "brick", "restore", "--locked", .. scope], cancellationToken);
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
