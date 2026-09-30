namespace Turian.Editor.Core;

/// <summary>One asset a brick exposes.</summary>
/// <param name="Id">The asset id, which scenes and other assets reference.</param>
/// <param name="Path">The asset's path inside the brick.</param>
/// <param name="TypeId">The type id of its meta, which says what kind of asset it is.</param>
public sealed record ContractAsset(Guid Id, string Path, string? TypeId);

/// <summary>One component or data asset class a brick exposes.</summary>
/// <param name="Id">The type id scenes store the class under.</param>
/// <param name="FullName">The class's full name.</param>
public sealed record ContractType(Guid Id, string FullName);

/// <summary>
/// What consumers of a brick can depend on: the ids of its assets and of its types. A brick keeps these stable across
/// versions, so a stub with the same contract can stand in for it while the real content is made elsewhere.
/// </summary>
/// <param name="Assets">The brick's assets.</param>
/// <param name="Types">The brick's classes.</param>
public sealed record BrickContract(IReadOnlyList<ContractAsset> Assets, IReadOnlyList<ContractType> Types)
{
    /// <summary>Reads the contract of a brick folder.</summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <returns>The contract.</returns>
    public static BrickContract Read(string packageRoot)
    {
        packageRoot = Path.GetFullPath(packageRoot);
        var assets = new List<ContractAsset>();
        foreach (var meta in Directory.EnumerateFiles(packageRoot, "*.meta", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(packageRoot, meta[..^".meta".Length]).Replace('\\', '/');
            if (relative.Split('/').SkipLast(1).Any(static s => s.EndsWith('~'))) continue;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(meta)) is JsonObject json && Guid.TryParse((string?)json["Id"], out var id))
                    assets.Add(new ContractAsset(id, relative, (string?)json["__TypeId"]));
            }
            catch (JsonException)
            {
                // A meta that is not JSON exposes nothing; verification reports it.
            }
        }

        var types = UserCodeTypeManifestGenerator.ScriptTypes(packageRoot, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .Select(static t => new ContractType(t.TypeId, t.FullyQualifiedName)).ToList();
        if (File.Exists(Path.Combine(packageRoot, BrickAssemblies.PrecastFolder, BrickAssemblies.TypesFile))
            && JsonSerializer.Deserialize<UserCodeTypeManifest>(File.ReadAllText(Path.Combine(packageRoot, BrickAssemblies.PrecastFolder, BrickAssemblies.TypesFile))) is { } precast)
        {
            types.AddRange(precast.Types.Where(t => types.All(known => known.Id != t.TypeId)).Select(static t => new ContractType(t.TypeId, t.FullyQualifiedName)));
        }

        return new BrickContract(assets, types);
    }

    /// <summary>What <paramref name="reference"/> exposes that this contract does not.</summary>
    /// <param name="reference">The contract to cover, such as the real brick's.</param>
    /// <returns>One line per asset or type missing or changed.</returns>
    public IReadOnlyList<string> Lacks(BrickContract reference)
    {
        var issues = new List<string>();
        var assets = Assets.ToDictionary(static a => a.Id);
        foreach (var asset in reference.Assets)
        {
            if (!assets.TryGetValue(asset.Id, out var mine)) issues.Add($"asset {asset.Id} ({asset.Path}) is missing");
            else if (mine.TypeId != asset.TypeId) issues.Add($"asset {asset.Id} ({asset.Path}) is type {mine.TypeId ?? "none"}, expected {asset.TypeId ?? "none"}");
        }

        var types = Types.ToDictionary(static t => t.Id);
        foreach (var type in reference.Types)
        {
            if (!types.TryGetValue(type.Id, out var mine)) issues.Add($"type {type.Id} ({type.FullName}) is missing");
            else if (mine.FullName != type.FullName) issues.Add($"type {type.Id} is {mine.FullName}, expected {type.FullName}");
        }

        return issues;
    }
}
