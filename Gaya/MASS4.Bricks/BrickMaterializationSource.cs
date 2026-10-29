using System.Text.RegularExpressions;

namespace MASS4.Bricks;

/// <summary>An immutable store, HTTPS git or checksum-pinned archive source for a producer checkout.</summary>
public sealed record BrickMaterializationSource
{
    /// <summary>The source kind: store, git or archive.</summary>
    public string Type { get; init; } = "store";

    /// <summary>The store brick's reverse-DNS identity.</summary>
    public string? Id { get; init; }

    /// <summary>The exact store version.</summary>
    public string? Version { get; init; }

    /// <summary>The HTTPS git or archive location.</summary>
    public string? Url { get; init; }

    /// <summary>The git commit, or a protected first-party release tag.</summary>
    public string? Ref { get; init; }

    /// <summary>The archive's SHA256 as 64 hexadecimal digits.</summary>
    public string? Sha256 { get; init; }

    /// <summary>The destination relative to the materialization root.</summary>
    public string? Path { get; init; }

    /// <summary>The legacy GitHub owner/repository alias.</summary>
    public string? Repository { get; init; }

    /// <summary>An optional store override for this dependency.</summary>
    [JsonPropertyName("store-url")]
    public string? StoreUrl { get; init; }

    static readonly Regex Commit = new("\\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\\z");
    static readonly Regex Hash = new("\\A[0-9a-fA-F]{64}\\z");
    static readonly Regex RepositoryName = new("\\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z");

    /// <summary>Reads a typed JSON dependency, the legacy GitHub shape, or an id@version store reference.</summary>
    /// <param name="text">The source description.</param>
    /// <returns>A validated source with legacy GitHub references expanded to HTTPS.</returns>
    public static BrickMaterializationSource Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var source = text.TrimStart().StartsWith('{') ? ParseJson(text) : ParseReference(text);
        source = NormalizeLegacy(source);
        source.Validate();
        return source;
    }

    static BrickMaterializationSource ParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            var source = JsonSerializer.Deserialize<BrickMaterializationSource>(text, options)!;
            if (!document.RootElement.TryGetProperty("type", out _))
            {
                if (source.Repository is null)
                {
                    throw new PackageException("A dependency needs a type: store, git or archive.");
                }

                source = source with { Type = "git" };
            }

            if (string.IsNullOrWhiteSpace(source.Path))
            {
                throw new PackageException("A JSON dependency needs a path under Bricks/.");
            }

            return source;
        }
        catch (JsonException ex)
        {
            throw new PackageException($"Invalid materialization source JSON: {ex.Message}", ex);
        }
    }

    static BrickMaterializationSource ParseReference(string text)
    {
        var separator = text.LastIndexOf('@');
        if (separator < 1)
        {
            throw new PackageException("Use <id>@<exact-version>, or a typed source JSON object.");
        }

        return new BrickMaterializationSource { Id = text[..separator], Version = text[(separator + 1)..] };
    }

    static BrickMaterializationSource NormalizeLegacy(BrickMaterializationSource source)
    {
        if (source.Repository is null)
        {
            return source;
        }

        if (source.Type != "git" || source.Url is not null || !RepositoryName.IsMatch(source.Repository)
            || source.Repository.Split('/').Any(static part => part is "." or ".."))
        {
            throw new PackageException("repository must be a GitHub owner/repo alias without a url.");
        }

        return source with { Url = $"https://github.com/{source.Repository}.git" };
    }

    /// <summary>Rejects unpinned sources, unsafe paths and credential-bearing or non-HTTPS fetch URLs.</summary>
    public void Validate()
    {
        ValidatePath();
        switch (Type)
        {
            case "store": ValidateStore(); break;
            case "git": ValidateGit(); break;
            case "archive": ValidateArchive(); break;
            default: throw new PackageException($"Unknown dependency type '{Type}'; use store, git or archive.");
        }
    }

    void ValidatePath()
    {
        if (Path is null)
        {
            return;
        }

        ValidateRelativePath(Path);
        if (Path.Split('/')[0].Equals("brick", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("A source-test dependency cannot replace the caller at Bricks/brick.");
        }
    }

    static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path)
            || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(static p => p is "" or "." or ".."))
        {
            throw new PackageException("Dependency path must be relative to Bricks/ without empty, '.' or '..' segments.");
        }
    }

    void ValidateStore()
    {
        if (!PackageId.IsValid(Id) || !SemanticVersion.TryParse(Version, out var version)
            || version.ToString() != Version || Url is not null || Ref is not null || Sha256 is not null)
        {
            throw new PackageException("A store source needs an id and an exact semantic version.");
        }
    }

    void ValidateGit()
    {
        ValidateGitFields();
        var git = Https(Url);
        if (Ref is null || !(Commit.IsMatch(Ref) || IsProtectedTag(git))
            || git.Query.Length > 0 || git.Fragment.Length > 0)
        {
            throw new PackageException("Pin git to a full commit SHA; protected v<semver> tags are allowed for MASS4ORG GitHub repositories.");
        }
    }

    void ValidateGitFields()
    {
        if (Id is not null || Version is not null || Sha256 is not null || StoreUrl is not null)
        {
            throw new PackageException("A git dependency cannot contain store or archive fields.");
        }
    }

    bool IsProtectedTag(Uri git) =>
        git.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && git.AbsolutePath.StartsWith("/MASS4ORG/", StringComparison.OrdinalIgnoreCase)
        && git.AbsolutePath.Trim('/').Split('/').Length == 2
        && Ref is not null && Ref.StartsWith('v')
        && SemanticVersion.TryParse(Ref[1..], out var tag) && tag.ToString() == Ref[1..];

    void ValidateArchive()
    {
        _ = Https(Url);
        if (Sha256 is null || !Hash.IsMatch(Sha256) || Id is not null || Version is not null
            || Ref is not null || StoreUrl is not null)
        {
            throw new PackageException("An archive source needs a mandatory SHA256 of 64 hexadecimal digits.");
        }
    }

    static Uri Https(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            ? uri
            : throw new PackageException("Dependency URLs must use HTTPS without embedded credentials.");
}
