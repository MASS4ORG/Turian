using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mass4.BrickRelease;

sealed record ArchiveIdentity(string PackageId, string Version, string Integrity);

sealed record Qualification(string PackageId, string Version, string Integrity, string SourceRevision);

static partial class ReleaseArchive
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static ArchiveIdentity Read(string archive)
    {
        using var packed = ZipFile.OpenRead(archive);
        var entries = packed.Entries.Where(static entry => entry.FullName == "package.json").ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException("Archive must contain exactly one root package.json.");
        }

        using var stream = entries[0].Open();
        using var manifest = JsonDocument.Parse(stream);
        var id = manifest.RootElement.GetProperty("name").GetString() ?? "";
        var version = manifest.RootElement.GetProperty("version").GetString() ?? "";
        if (!IdPattern().IsMatch(id) || !VersionPattern().IsMatch(version))
        {
            throw new InvalidDataException("Invalid brick ID or version.");
        }

        using var bytes = File.OpenRead(archive);
        return new ArchiveIdentity(id, version, "sha256-" + Convert.ToBase64String(SHA256.HashData(bytes)));
    }

    internal static Qualification Qualify(string archive, string tag, string revision, string output,
        string? githubOutput = null)
    {
        var identity = Read(archive);
        if (tag != "v" + identity.Version || !RevisionPattern().IsMatch(revision))
        {
            throw new InvalidDataException("Tag must match the archive version; source revision must be a full commit SHA.");
        }

        var qualification = new Qualification(identity.PackageId, identity.Version, identity.Integrity, revision);
        File.WriteAllText(output, JsonSerializer.Serialize(qualification, Json) + "\n");
        if (githubOutput is not null)
        {
            WriteGithubOutput(archive, qualification, githubOutput);
        }

        return qualification;
    }

    internal static Qualification Validate(string archive, string qualificationFile)
    {
        var expected = JsonSerializer.Deserialize<Qualification>(File.ReadAllText(qualificationFile), Json)
                       ?? throw new InvalidDataException("Missing archive qualification.");
        var identity = Read(archive);
        if (identity != new ArchiveIdentity(expected.PackageId, expected.Version, expected.Integrity)
            || !RevisionPattern().IsMatch(expected.SourceRevision ?? ""))
        {
            throw new InvalidDataException("Archive identity or integrity differs from qualification.");
        }

        return expected;
    }

    static void WriteGithubOutput(string archive, Qualification qualification, string output)
    {
        var name = Path.GetFileName(archive);
        if (name != $"{qualification.PackageId}-{qualification.Version}.brick")
        {
            throw new InvalidDataException("Archive filename must match its ID and version.");
        }

        var compact = JsonSerializer.Serialize(qualification, new JsonSerializerOptions(Json) { WriteIndented = false });
        File.AppendAllText(output, $"qualification={compact}\narchive={name}\n");
    }

    [GeneratedRegex(@"\A[a-z0-9]+(?:[.-][a-z0-9]+)+\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z")]
    private static partial Regex RevisionPattern();
}
