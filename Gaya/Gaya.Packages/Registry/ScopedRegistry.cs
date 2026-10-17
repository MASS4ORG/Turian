namespace Gaya.Packages;

/// <summary>
/// A registry a project takes bricks from, for the names it is scoped to: a studio's private registry beside the public
/// one. The keys it is trusted with are stored in the project; when adding a registry the user may accept the ones it
/// publishes, but a registry can never replace them afterwards.
/// </summary>
public sealed class ScopedRegistry
{
    /// <summary>A name for the registry, shown in messages and used to look up its access token.</summary>
    [SetOrder(-10)]
    [Tooltip("Optional: the address's host when left empty.")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where the registry is: the <c>https://…/v1</c> address, or a folder of a static copy such as a mirror or a
    /// release asset unpacked for offline use.
    /// </summary>
    [SetOrder(-20)]
    [Tooltip("The registry's address, such as store.mass4.org; the rest is read from it when left empty.")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Other addresses of the same registry, tried in order when <see cref="Url"/> cannot be reached.</summary>
    [Tooltip("Fallback addresses serving the same bricks, signed by the same keys.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string>? Mirrors { get; set; }

    /// <summary>The name prefixes served from here: <c>org.mass4</c> covers <c>org.mass4</c> and <c>org.mass4.games.ui</c>.</summary>
    [Tooltip("Optional: which brick names come from here, such as org.mass4; read from the registry when left empty.")]
    public List<string> Scopes { get; set; } = [];

    /// <summary>The keys whose signatures are trusted, as OpenSSH public key lines.</summary>
    [Tooltip("Optional: trusted OpenSSH ed25519 public keys; the registry's own keys are trusted when left empty. A registry cannot replace them later.")]
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
