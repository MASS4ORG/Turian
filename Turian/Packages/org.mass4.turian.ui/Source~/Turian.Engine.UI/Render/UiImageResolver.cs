namespace Turian.Engine.UI;

/// <summary>
/// Resolves a <c>.ui</c> image reference — a project-relative path or <c>asset://&lt;guid&gt;</c> —
/// to a decoded <see cref="SKImage"/> for the renderer to draw. Decoded images are cached by the
/// reference string for the resolver's lifetime.
/// </summary>
/// <remarks>
/// The source file is preferred (loose projects, the editor, the CLI). When only the imported
/// artifact is available — a packed game — a raw-image artifact still decodes; a baked
/// <c>.amtex</c> block does not and yields <c>null</c> until the world-space texture path is wired
/// through here.
/// </remarks>
public sealed class UiImageResolver
{
    readonly Dictionary<string, SKImage?> cache = new(StringComparer.Ordinal);
    readonly AssetDatabase? database;
    readonly List<string> roots;

    /// <summary>Creates a resolver.</summary>
    /// <param name="database">Asset database for path/id lookups, or <c>null</c> when there is no project.</param>
    /// <param name="roots">Extra directories to probe a relative path against, in order.</param>
    public UiImageResolver(AssetDatabase? database, params string[] roots)
    {
        this.database = database;
        this.roots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))];
    }

    /// <summary>Resolves <paramref name="src"/> to an image, or <c>null</c>.</summary>
    /// <param name="src">A project-relative path or <c>asset://&lt;guid&gt;</c>.</param>
    public SKImage? Resolve(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        if (cache.TryGetValue(src, out var cached)) return cached;

        var image = Load(src.Trim());
        cache[src] = image;
        return image;
    }

    SKImage? Load(string src)
    {
        if (src.StartsWith("asset://", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(src.AsSpan("asset://".Length), out var assetId))
        {
            return DecodeFromDatabase(assetId);
        }

        var normalized = src.Replace('\\', '/');

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
                return File.Exists(absolute) ? Decode(absolute) : DecodeFromDatabase(record.AssetId);
            }
        }

        foreach (var root in roots)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, normalized));
            if (File.Exists(absolute)) return Decode(absolute);
        }

        return null;
    }

    static SKImage? Decode(string path)
    {
        try
        {
            using var data = SKData.Create(path);
            return data is null ? null : SKImage.FromEncodedData(data);
        }
        catch (IOException)
        {
            return null;
        }
    }

    SKImage? DecodeFromDatabase(Guid assetId)
    {
        if (database is null || !database.TryGetAssetProvider(assetId, out var provider) || provider is null)
            return null;

        try
        {
            using var stream = provider.GetAssetStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return SKImage.FromEncodedData(buffer.ToArray());
        }
        catch (IOException)
        {
            return null;
        }
    }
}
