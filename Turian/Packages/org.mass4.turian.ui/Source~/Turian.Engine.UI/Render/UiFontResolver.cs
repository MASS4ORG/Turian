namespace Turian.Engine.UI;

/// <summary>
/// Resolves a <c>.ui</c> / <c>.uss</c> font reference — a project-relative <c>.ttf</c>/<c>.otf</c>
/// path, an <c>asset://&lt;guid&gt;</c>, or a bare family name — to a Guinevere <see cref="Font"/>.
/// Loaded typefaces are cached; callers set the point size with <see cref="Font.WithSize"/>.
/// </summary>
public sealed class UiFontResolver
{
    readonly Dictionary<string, Font?> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly AssetDatabase? database;
    readonly List<string> roots;

    /// <summary>Creates a resolver.</summary>
    /// <param name="database">Asset database for path/id lookups, or <c>null</c>.</param>
    /// <param name="roots">Extra directories to probe a relative path against.</param>
    public UiFontResolver(AssetDatabase? database, params string[] roots)
    {
        this.database = database;
        this.roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))];
    }

    /// <summary>Resolves <paramref name="reference"/> to a base font, or <c>null</c>.</summary>
    /// <param name="reference">A font path, <c>asset://&lt;guid&gt;</c>, or family name.</param>
    public Font? Resolve(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var key = reference.Trim().Trim('"', '\'');
        if (cache.TryGetValue(key, out var cached)) return cached;

        var font = Load(key);
        cache[key] = font;
        return font;
    }

    Font? Load(string reference)
    {
        if (reference.StartsWith("asset://", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(reference.AsSpan("asset://".Length), out var assetId))
        {
            return FromDatabaseStream(assetId);
        }

        var looksLikePath = reference.Contains('/', StringComparison.Ordinal)
                            || reference.Contains('\\', StringComparison.Ordinal)
                            || reference.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                            || reference.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

        if (looksLikePath)
        {
            var normalized = reference.Replace('\\', '/');

            if (database is not null)
            {
                foreach (var record in database.GetAssetsSnapshot())
                {
                    if (!string.Equals(record.SourceRelativePath.Replace('\\', '/'), normalized,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var absolute = Path.Combine(record.ProjectRootPath, record.SourceRelativePath);
                    return File.Exists(absolute) ? FromFile(absolute) : FromDatabaseStream(record.AssetId);
                }
            }

            foreach (var root in roots)
            {
                var absolute = Path.GetFullPath(Path.Combine(root, normalized));
                if (File.Exists(absolute)) return FromFile(absolute);
            }

            return null;
        }

        // A bare family name.
        try
        {
            return Font.FromFamilyName(reference);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static Font? FromFile(string path)
    {
        try
        {
            return Font.FromFile(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    Font? FromDatabaseStream(Guid assetId)
    {
        if (database is null || !database.TryGetAssetProvider(assetId, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return Font.FromStream(buffer);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
