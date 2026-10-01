namespace Gaya.Packages;

/// <summary>
/// A registry a project takes bricks from, for the names it is scoped to: a studio's private registry beside the public
/// one. The keys it is trusted with are the project's choice, never read from the registry itself.
/// </summary>
public sealed class ScopedRegistry
{
    /// <summary>A name for the registry, shown in messages and used to look up its access token.</summary>
    [InspectorOrder(-20)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where the registry is: the <c>https://…/v1</c> address, or a folder of a static copy such as a mirror or a
    /// release asset unpacked for offline use.
    /// </summary>
    [InspectorOrder(-10)]
    public string Url { get; set; } = string.Empty;

    /// <summary>The name prefixes served from here: <c>org.mass4</c> covers <c>org.mass4</c> and <c>org.mass4.games.ui</c>.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>The keys whose signatures are trusted, as OpenSSH public key lines.</summary>
    [Tooltip("Trusted OpenSSH ed25519 public keys. A registry cannot authorize its own replacement key.")]
    public List<string> Keys { get; set; } = [];

    /// <summary>Accept bricks without a signature, which only a registry the studio runs itself should be allowed.</summary>
    [Tooltip("Explicitly permits unsigned bricks from this registry.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllowUnsigned { get; set; }

    /// <summary>Whether <paramref name="id"/> is served by this registry.</summary>
    /// <param name="id">The brick id.</param>
    /// <returns>True when a scope equals the id or is a dot-separated prefix of it.</returns>
    public bool Serves(string id) =>
        Scopes.Any(scope => id == scope || id.StartsWith(scope + ".", StringComparison.Ordinal));
}
