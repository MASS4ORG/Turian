using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mass4.BrickRelease;

sealed record ArchiveIdentity(string PackageId, string Version, string Integrity);

sealed record Qualification(string PackageId, string Version, string Integrity, string SourceRevision);

/// <summary>The unsigned build provenance the store records with a release: the digest-pinned image and its Turian version.</summary>
sealed record ReleaseBuild(string Image, string TurianVersion);

static partial class ReleaseArchive
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static ArchiveIdentity Read(string archive) => Read(archive, out _);

    /// <summary>Reads an archive's signed identity and, from its manifest alone, which variant of the release it is.</summary>
    internal static ArchiveIdentity Read(string archive, out string variant)
    {
        using var packed = ZipFile.OpenRead(archive);
        var entries = packed.Entries.Where(static entry => entry.FullName == "package.json").ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException("Archive must contain exactly one root package.json.");
        }

        using var stream = entries[0].Open();
        using var manifest = JsonDocument.Parse(stream);
        var id = (manifest.RootElement.TryGetProperty("id", out var v2) ? v2 : manifest.RootElement.GetProperty("name")).GetString() ?? "";
        var version = manifest.RootElement.GetProperty("version").GetString() ?? "";
        if (!IdPattern().IsMatch(id) || !VersionPattern().IsMatch(version))
        {
            throw new InvalidDataException("Invalid brick ID or version.");
        }

        variant = VariantOf(manifest.RootElement);
        using var bytes = File.OpenRead(archive);
        return new ArchiveIdentity(id, version, "sha256-" + Convert.ToBase64String(SHA256.HashData(bytes)));
    }

    /// <summary>The manifest's declared variant, <c>source</c> without one, and empty for a bake that declares none.</summary>
    static string VariantOf(JsonElement manifest) => manifest.TryGetProperty("variant", out var declared)
        ? declared.GetString() ?? ""
        : manifest.TryGetProperty("bakeOf", out _) ? "" : "source";

    /// <summary>Checks build provenance as the store does: an image pinned by sha256 digest and an exact version.</summary>
    internal static ReleaseBuild ValidateBuild(string image, string turianVersion) =>
        PinnedImagePattern().IsMatch(image) && VersionPattern().IsMatch(turianVersion)
            ? new ReleaseBuild(image, turianVersion)
            : throw new InvalidDataException("Build image must be <name>@sha256:<64 lowercase hex digits> and the Turian version exact.");

    internal static Qualification Qualify(string archive, string tag, string revision, string output,
        string? githubOutput = null, string outputPrefix = "")
    {
        var identity = Read(archive, out var variant);
        if (tag != "v" + identity.Version || !RevisionPattern().IsMatch(revision))
        {
            throw new InvalidDataException("Tag must match the archive version; source revision must be a full commit SHA.");
        }

        var qualification = new Qualification(identity.PackageId, identity.Version, identity.Integrity, revision);
        File.WriteAllText(output, JsonSerializer.Serialize(qualification, Json) + "\n");
        if (githubOutput is not null)
        {
            WriteGithubOutput(archive, qualification, variant, githubOutput, outputPrefix);
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

    static void WriteGithubOutput(string archive, Qualification qualification, string variant, string output, string prefix)
    {
        var name = Path.GetFileName(archive);
        if (!VariantPattern().IsMatch(variant) || name != $"{qualification.PackageId}-{qualification.Version}--{variant}.brick")
        {
            throw new InvalidDataException("Archive filename must be <id>-<version>--<variant>.brick, as its manifest declares.");
        }

        if (!OutputPrefixPattern().IsMatch(prefix))
        {
            throw new InvalidDataException("Output prefix must be lowercase letters and dashes.");
        }

        var compact = JsonSerializer.Serialize(qualification, new JsonSerializerOptions(Json) { WriteIndented = false });
        File.AppendAllText(output, $"{prefix}qualification={compact}\n{prefix}archive={name}\n");
    }

    [GeneratedRegex(@"\A(?:[a-z]+-)*\z")]
    private static partial Regex OutputPrefixPattern();

    [GeneratedRegex(@"\A[a-z0-9]+(?:[.-][a-z0-9]+)+\z")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z")]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"\A(?!legacy-)[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?\z")]
    private static partial Regex VariantPattern();

    [GeneratedRegex(@"\A[a-z0-9]+(?:[._-]+[a-z0-9]+)*(?::[0-9]+)?(?:/[a-z0-9]+(?:[._-]+[a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?@sha256:[0-9a-f]{64}\z")]
    private static partial Regex PinnedImagePattern();
}
