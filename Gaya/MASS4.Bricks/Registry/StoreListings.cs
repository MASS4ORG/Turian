namespace MASS4.Bricks;

/// <summary>
/// Reads how a store presents its bricks, from its public package pages: each version's manifest, archive size and
/// deprecation. Only a registry that is also a store has them; for any other, and on any failure, nothing is added.
/// </summary>
static class StoreListings
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>What a store says about one version.</summary>
    /// <param name="ManifestJson">The version's <c>package.json</c> text.</param>
    /// <param name="Size">The archive's size in bytes.</param>
    /// <param name="Deprecation">Why consumers should move off it, or null.</param>
    internal sealed record VersionDetail(string? ManifestJson, long? Size, string? Deprecation);

    /// <summary>What a store says about one brick, by version.</summary>
    /// <param name="Versions">Version → its detail.</param>
    internal sealed record Detail(IReadOnlyDictionary<string, VersionDetail> Versions);

    /// <summary>Reads the store's page of each brick; a registry that is not a store answers nothing.</summary>
    /// <param name="registry">The registry, whose address names the store.</param>
    /// <param name="ids">The bricks to describe.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Brick id → its detail, for the bricks the store described.</returns>
    internal static async Task<IReadOnlyDictionary<string, Detail>> DescribeAsync(ScopedRegistry registry,
        IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, Detail>(StringComparer.Ordinal);
        if (ids.Count == 0 || !TryOrigin(registry, out var origin))
        {
            return found;
        }

        var token = RegistryClient.TokenFor(registry);
        // The catalog tells whether the registry is a store at all, before asking about every brick.
        if (await GetAsync(origin, token, "api/v1/packages?pageSize=1", cancellationToken).ConfigureAwait(false) is null)
        {
            return found;
        }

        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (id, ct) =>
            {
                if (await GetAsync(origin, token, $"api/v1/packages/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false) is { } text
                    && Parse(text) is { } detail)
                {
                    found[id] = detail;
                }
            }).ConfigureAwait(false);
        return found;
    }

    static bool TryOrigin(ScopedRegistry registry, out Uri origin)
    {
        origin = null!;
        if (!registry.Url.StartsWith("http://", StringComparison.Ordinal) && !registry.Url.StartsWith("https://", StringComparison.Ordinal))
        {
            return false;
        }

        origin = StoreClient.OriginOf(registry.Url);
        return true;
    }

    // The body of a successful answer, or null for any failure.
    static async Task<string?> GetAsync(Uri origin, string? token, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Reads a store's package page; null when the text is not one.</summary>
    /// <param name="json">The page's text.</param>
    /// <returns>The detail, or null.</returns>
    static Detail? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject { } page || page["versions"] is not JsonArray versions)
            {
                return null;
            }

            var details = new Dictionary<string, VersionDetail>(StringComparer.Ordinal);
            foreach (var entry in versions.OfType<JsonObject>())
            {
                if (Text(entry, "version") is { } version)
                {
                    details[version] = new VersionDetail(Text(entry, "manifestJson"),
                        entry["sizeBytes"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : null,
                        Text(entry, "deprecationReason"));
                }
            }

            return new Detail(details);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
