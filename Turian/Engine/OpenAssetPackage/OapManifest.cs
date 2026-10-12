namespace OpenAssetPackage;

/// <summary>
/// The conventional fields of an OAP manifest (opaque UTF-8 JSON in the container).
/// Unknown fields are ignored and preserved verbatim in the package.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Version">The package version string.</param>
/// <param name="Generator">The tool that produced the package.</param>
/// <param name="Requires">Names of other packages that must be mounted alongside this one.</param>
public sealed record OapManifest(string Name, string Version, string Generator, IReadOnlyList<string> Requires)
{
    /// <summary>Parses a manifest blob, returning <see langword="null"/> when it is absent or not JSON.</summary>
    /// <param name="bytes">The manifest bytes, or <see langword="null"/>.</param>
    /// <returns>The parsed manifest, or <see langword="null"/>.</returns>
    public static OapManifest? TryParse(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is not { } blob || blob.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(blob);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var requires = ParseRequires(root);
            if (requires is null) return null;

            return new OapManifest(
                GetString(root, "name"),
                GetString(root, "version"),
                GetString(root, "generator"),
                requires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static List<string>? ParseRequires(JsonElement root)
    {
        var requires = new List<string>();
        if (!root.TryGetProperty("requires", out var values)) return requires;
        if (values.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { } value || string.IsNullOrWhiteSpace(value))
                return null;
            requires.Add(value);
        }
        return requires;
    }

    static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
