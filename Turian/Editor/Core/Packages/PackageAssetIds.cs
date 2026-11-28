using MASS4.Bricks;

namespace Turian.Editor.Core;

/// <summary>
/// Checks that no package reuses an asset id. Scenes reference assets by id alone, so a package reusing one — two
/// versions of one package, or a copied folder — would make references ambiguous.
/// </summary>
public static class PackageAssetIds
{
    const string projectOwner = "the project";

    /// <summary>
    /// Fails when a package's meta file shares an asset id with another meta file. Ids repeated within the project's
    /// <c>Assets</c> are left to the asset database, so they never cost the project its packages.
    /// </summary>
    /// <param name="assetsDirectory">The project's <c>Assets</c> folder.</param>
    /// <param name="packages">The resolved packages.</param>
    /// <exception cref="PackageException">A package's id is claimed twice.</exception>
    public static void EnsureUnique(string assetsDirectory, IReadOnlyList<ResolvedPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
        {
            return;
        }

        var owners = new Dictionary<Guid, string>();
        var roots = packages.Select(static p => (p.RootPath, Owner: $"package {p.Id}"))
            .Prepend((assetsDirectory, Owner: projectOwner));

        foreach (var (root, owner) in roots.Where(static r => Directory.Exists(r.Item1)))
        {
            foreach (var meta in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories)
                         .Where(meta => !AssetDatabase.IsInTildeFolder(meta, root)))
            {
                if (ReadId(meta) is not { } id)
                {
                    continue;
                }

                var claim = $"{owner} ({meta})";
                if (!owners.TryAdd(id, claim) && owner != projectOwner)
                {
                    throw new PackageException($"Asset id {id} is claimed by {owners[id]} and by {claim}.");
                }
            }
        }
    }

    static Guid? ReadId(string metaPath)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(metaPath));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

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
