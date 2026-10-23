namespace MASS4.Bricks;

/// <summary>
/// A registry a project takes bricks from, optionally filtered by brick name prefixes. Its trusted keys are stored in
/// the project; the user may accept the keys it publishes when adding it, but the registry cannot replace them later.
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

    /// <summary>Optional name prefix filters; empty or <c>*</c> accepts all names, and <c>org.mass4</c> includes its dot-separated descendants.</summary>
    [Tooltip("Optional: filter brick names by prefixes such as org.mass4. Empty or * accepts all names.")]
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
    /// <returns>True when unfiltered, a wildcard is present, or a scope equals the id or is a dot-separated prefix.</returns>
    public bool Serves(string id) =>
        Scopes.Count == 0 || Scopes.Any(scope => scope == "*" || id == scope
            || id.StartsWith(scope + ".", StringComparison.Ordinal));
}
