namespace Gaya.Packages;

/// <summary>A registry's <c>v1/index.json</c>: every brick it serves, version by version, and who owns which names.</summary>
public sealed class RegistryIndex
{
    /// <summary>The format version of this file.</summary>
    public int Format { get; set; } = 1;

    /// <summary>When the index was last written.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>Brick id → the versions published under it.</summary>
    public SortedDictionary<string, RegistryBrick> Bricks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Name prefix → the publisher key that owns it, first publish wins.</summary>
    public SortedDictionary<string, RegistryClaim> Claims { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads an index.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The index.</returns>
    /// <exception cref="PackageException">The text is not an index.</exception>
    public static RegistryIndex Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RegistryIndex>(json, PackageJson.Options) ?? throw new PackageException("The registry index is empty.");
        }
        catch (JsonException ex)
        {
            throw new PackageException($"The registry index is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>The index as text.</summary>
    /// <returns>The JSON.</returns>
    public string Serialize() => JsonSerializer.Serialize(this, PackageJson.Options);
}

/// <summary>The versions of one brick in a registry.</summary>
public sealed class RegistryBrick
{
    /// <summary>Version → its entry.</summary>
    public SortedDictionary<string, RegistryVersion> Versions { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One published version of a brick.</summary>
public sealed class RegistryVersion
{
    /// <summary>The <c>.brick</c> file: a path relative to the registry's location, or an absolute URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The file's content hash, <c>sha256-</c> + base64.</summary>
    public string Integrity { get; set; } = string.Empty;

    /// <summary>The registry's signature over <see cref="RegistrySignatures.Message"/>: the armored text, base64 only.</summary>
    public string? Signature { get; set; }

    /// <summary>The fingerprint of the key that made <see cref="Signature"/>.</summary>
    public string? SignedBy { get; set; }

    /// <summary>The publisher's own signature over the same message, for names the registry has claimed for them.</summary>
    public string? PublisherSignature { get; set; }

    /// <summary>The fingerprint of the publisher's key.</summary>
    public string? PublisherKey { get; set; }

    /// <summary>When the version was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The brick's own dependencies, as its manifest declares them, so a host can plan without downloading.</summary>
    public Dictionary<string, string> Dependencies { get; set; } = [];

    /// <summary>The host versions the brick works with, as its manifest declares them.</summary>
    public Dictionary<string, VersionRange> Engines { get; set; } = [];

    /// <summary>Licensing and access terms; downloads of an entitled brick need a token.</summary>
    public PackageStoreInfo? Store { get; set; }

    /// <summary>Withdrawn: still downloadable for projects that locked it, never chosen for a new resolution.</summary>
    public bool Yanked { get; set; }
}

/// <summary>A name prefix a publisher owns.</summary>
public sealed class RegistryClaim
{
    /// <summary>The fingerprint of the publisher's key.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>A registry's public keys, as served at <c>v1/keys</c>.</summary>
public sealed class RegistryKeys
{
    /// <summary>The keys.</summary>
    public List<RegistryKey> Keys { get; set; } = [];

    /// <summary>Reads a key list.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The keys.</returns>
    /// <exception cref="PackageException">The text is not a key list.</exception>
    public static RegistryKeys Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RegistryKeys>(json, PackageJson.Options) ?? new RegistryKeys();
        }
        catch (JsonException ex)
        {
            throw new PackageException($"The registry key list is not valid: {ex.Message}", ex);
        }
    }
}

/// <summary>One public key of a registry.</summary>
public sealed class RegistryKey
{
    /// <summary>A name for the key, such as <c>mass4-2026</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The key as an OpenSSH public key line.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>The name prefixes the key may sign for; every name when empty.</summary>
    public List<string> Scopes { get; set; } = [];
}

/// <summary>Licensing and access terms of a brick: the placeholders paid distribution builds on.</summary>
public sealed class PackageStoreInfo
{
    /// <summary>Whether downloads need an access token: <c>token</c>; none when absent.</summary>
    public string? Entitlement { get; set; }

    /// <summary>Where the end-user license agreement is.</summary>
    public string? Eula { get; set; }

    /// <summary>Whether a project may republish the brick, for example inside an embedded fork; true when absent.</summary>
    public bool Redistribute { get; set; } = true;

    /// <summary>Whether a project may fork the brick with <c>embed</c>; true when absent.</summary>
    public bool Embeddable { get; set; } = true;
}

/// <summary>What registries sign and how.</summary>
public static class RegistrySignatures
{
    /// <summary>The namespace the registry's signature is made under.</summary>
    public const string RegistryNamespace = "bricks@mass4.org";

    /// <summary>The namespace a publisher's signature is made under.</summary>
    public const string PublisherNamespace = "brick-publisher@mass4.org";

    /// <summary>The bytes a signature covers: the brick's id, version and content hash.</summary>
    /// <param name="id">The brick id.</param>
    /// <param name="version">The version.</param>
    /// <param name="integrity">The file's content hash.</param>
    /// <returns>The message.</returns>
    public static byte[] Message(string id, string version, string integrity) =>
        Encoding.UTF8.GetBytes($"{id}@{version}\n{integrity}\n");

    /// <summary>The signature's base64 body, without the armor lines.</summary>
    /// <param name="armored">The armored signature.</param>
    /// <returns>The base64 text.</returns>
    public static string Strip(string armored) =>
        string.Concat(armored.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static l => !l.StartsWith("-----", StringComparison.Ordinal)));
}
