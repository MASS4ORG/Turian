using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// Checks that every asset id in a project and its packages is claimed once. Scenes reference assets by id alone,
/// so a package reusing an id — two versions of one package, or a copied folder — would make references ambiguous.
/// </summary>
public static class PackageAssetIds
{
    /// <summary>Fails when two meta files, in the project's <c>Assets</c> or its packages, share an asset id.</summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="packages">The resolved packages.</param>
    /// <exception cref="PackageException">An id is claimed twice.</exception>
    public static void EnsureUnique(string assetsDirectory, IReadOnlyList<ResolvedPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0) return;

        var owners = new Dictionary<Guid, string>();
        var roots = packages.Select(static p => (p.RootPath, Owner: $"package {p.Id}"))
            .Prepend((assetsDirectory, Owner: "the project"));

        foreach (var (root, owner) in roots.Where(static r => Directory.Exists(r.Item1)))
        {
            foreach (var meta in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
                         .Where(meta => !AssetDatabase.IsInTildeFolder(meta, root)))
            {
                if (ReadId(meta) is not { } id) continue;

                var claim = $"{owner} ({meta})";
                if (!owners.TryAdd(id, claim))
                    throw new PackageException($"Asset id {id} is claimed by {owners[id]} and by {claim}.");
            }
        }
    }

    static Guid? ReadId(string metaPath)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(metaPath));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals("Id"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                return reader.Read() && reader.TryGetGuid(out var id) ? id : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogDebug(ex, "{Path} is not a readable meta file", metaPath);
        }

        return null;
    }
}
