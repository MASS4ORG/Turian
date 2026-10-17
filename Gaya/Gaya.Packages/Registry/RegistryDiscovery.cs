namespace Gaya.Packages;

/// <summary>
/// Completes a registry from the address a user types, such as <c>store.mass4.org</c>: finds where its index is, names it
/// after its host, scopes it to the name prefixes it serves and proposes the keys it publishes. The caller shows the keys'
/// fingerprints so the user decides whether to trust them.
/// </summary>
public static class RegistryDiscovery
{
    /// <summary>The paths, below a host, where a registry's index is looked for, in order.</summary>
    static readonly string[] Paths = ["", "v1", "api/v1/registry/v1"];

    /// <summary>Finds the registry at <paramref name="address"/>.</summary>
    /// <param name="address">A host, a URL or a local folder; without a scheme https is tried before http.</param>
    /// <param name="http">The HTTP client to use; a shared one when null.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The registry, with no scope when it does not say which names it serves.</returns>
    /// <exception cref="PackageException">No registry answers at any address derived from <paramref name="address"/>.</exception>
    public static async Task<ScopedRegistry> DiscoverAsync(string address, HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        address = address.Trim().TrimEnd('/');
        foreach (var location in Candidates(address))
        {
            var registry = new ScopedRegistry { Name = NameOf(location), Url = location };
            var client = new RegistryClient(registry, http);
            RegistryIndex index;
            try
            {
                index = await client.GetIndexAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PackageException)
            {
                continue;
            }

            var keys = await KeysAsync(client, cancellationToken).ConfigureAwait(false);
            registry.Keys = [.. keys.Keys.Select(static k => k.PublicKey).Where(static k => k.Length > 0).Distinct(StringComparer.Ordinal)];
            registry.Scopes = ScopesOf(index, keys);
            return registry;
        }

        throw new PackageException(
            $"No brick registry answers at {address}; tried {string.Join(", ", Candidates(address))}. Check the address and its port.");
    }

    /// <summary>
    /// Fills what a user left out of a registry from the registry itself: its location, name, scopes and keys. What the
    /// user entered is kept; a registry already complete is returned as it is, without a request.
    /// </summary>
    /// <param name="typed">The registry as entered; only its address is required.</param>
    /// <param name="http">The HTTP client to use; a shared one when null.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>The completed registry.</returns>
    /// <exception cref="PackageException">There is no address, no registry answers at it, or it does not say which names it serves.</exception>
    public static async Task<ScopedRegistry> CompleteAsync(ScopedRegistry typed, HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (string.IsNullOrWhiteSpace(typed.Url))
        {
            throw new PackageException("Enter the registry's address under Url, such as store.mass4.org.");
        }

        var trusted = typed.Keys.Count > 0 || typed.AllowUnsigned;
        if (typed.Scopes.Count > 0 && trusted && typed.Name.Length > 0)
        {
            return typed;
        }

        return Merge(typed, await DiscoverAsync(typed.Url, http, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The registry as typed, with what was left out taken from the one found at its address.</summary>
    static ScopedRegistry Merge(ScopedRegistry typed, ScopedRegistry found)
    {
        var scopes = typed.Scopes.Count > 0 ? typed.Scopes : found.Scopes;
        if (scopes.Count == 0)
        {
            throw new PackageException(
                $"{found.Url} does not say which bricks it serves yet; enter the brick name prefixes to take from it under Scopes, such as com.acme.");
        }

        return new ScopedRegistry
        {
            Name = typed.Name.Length > 0 ? typed.Name : found.Name,
            Url = found.Url,
            Mirrors = typed.Mirrors,
            Scopes = scopes,
            Keys = typed.Keys.Count > 0 || typed.AllowUnsigned ? typed.Keys : found.Keys,
            AllowUnsigned = typed.AllowUnsigned,
        };
    }

    /// <summary>The addresses tried for what the user typed: as given, then with each registry path appended.</summary>
    /// <param name="address">The typed address, without a trailing slash.</param>
    /// <returns>The candidate registry locations, in order.</returns>
    public static IEnumerable<string> Candidates(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Directory.Exists(address) || address.StartsWith("file:", StringComparison.Ordinal))
        {
            return [address];
        }

        string[] bases = address.Contains("://", StringComparison.Ordinal) ? [address] : [$"https://{address}", $"http://{address}"];
        return bases.SelectMany(static b => Paths.Select(p => p.Length == 0 ? b : $"{b}/{p}")).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// The prefixes a registry serves: those it records owners for, else the keys' own scopes, else the two leading
    /// segments of each brick it holds.
    /// </summary>
    /// <param name="index">The registry's index.</param>
    /// <param name="keys">The registry's keys.</param>
    /// <returns>The prefixes, sorted; empty when the registry is empty and says nothing.</returns>
    public static List<string> ScopesOf(RegistryIndex index, RegistryKeys keys)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(keys);
        var scopes = index.Claims.Keys.ToList();
        if (scopes.Count == 0)
        {
            scopes = [.. keys.Keys.SelectMany(static k => k.Scopes)];
        }

        if (scopes.Count == 0)
        {
            scopes = [.. index.Bricks.Keys.Select(static id => string.Join('.', id.Split('.').Take(2)))];
        }

        return [.. scopes.Where(static s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    static string NameOf(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.Host : Path.GetFileName(location);

    static async Task<RegistryKeys> KeysAsync(RegistryClient client, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetKeysAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PackageException)
        {
            return new RegistryKeys();
        }
    }
}
