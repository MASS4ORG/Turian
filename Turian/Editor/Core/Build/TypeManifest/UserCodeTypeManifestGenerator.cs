namespace Turian.Editor.Core;

/// <summary>
/// Generates a <see cref="UserCodeTypeManifest"/> by walking the user Assets directory,
/// pairing each <c>.cs</c> file with the primary class it declares and using its <c>.cs.meta</c>
/// AssetId as the stable TypeId. A script without a <c>.cs.meta</c> gets one on first sight.
///
/// Only syntactic parsing is used — no Roslyn compilation is required — so this runs fast
/// and works for both fresh and cached compilation paths.
/// </summary>
public static class UserCodeTypeManifestGenerator
{
    /// <summary>
    /// Generates a manifest by scanning <paramref name="assetsDirectory"/> recursively for
    /// <c>.cs</c> files that declare a class.
    /// </summary>
    /// <param name="assetsDirectory">The project's Assets folder.</param>
    /// <param name="logger">Receives parse and meta-file problems.</param>
    /// <param name="assemblyName">The user assembly the types compile into, recorded so a runtime can load it.</param>
    public static UserCodeTypeManifest Generate(string assetsDirectory, ILogger logger, string? assemblyName = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var manifest = new UserCodeTypeManifest { AssemblyName = assemblyName };

        if (!Directory.Exists(assetsDirectory))
        {
            logger.LogWarning("Assets directory not found: {Dir}. Type manifest will be empty", assetsDirectory);
            return manifest;
        }

        foreach (var csFilePath in Directory.EnumerateFiles(assetsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var fqn = ExtractPrimaryClassFqn(csFilePath, logger);
            if (fqn is null)
                continue;

            var metaPath = csFilePath + ".meta";
            var typeId = File.Exists(metaPath)
                ? ReadAssetId(metaPath, logger)
                : CreateScriptMeta(csFilePath, assetsDirectory, logger);
            if (typeId == Guid.Empty)
                continue;

            manifest.Types.Add(new UserCodeTypeEntry { FullyQualifiedName = fqn, TypeId = typeId });
            logger.LogDebug("TypeManifest: {Fqn} → {TypeId}", fqn, typeId);
        }

        logger.LogInformation("Type manifest generated: {Count} entry(ies)", manifest.Types.Count);
        return manifest;
    }

    /// <summary>
    /// Writes the <c>.cs.meta</c> that gives a new script its TypeId. Scenes store components by that
    /// id, so without it a component declared in the script could not be saved.
    /// </summary>
    static Guid CreateScriptMeta(string csFilePath, string assetsDirectory, ILogger logger)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(assetsDirectory)) ?? assetsDirectory;
        var meta = new Asset
        {
            Id = Guid.NewGuid(),
            RelativePath = Path.GetRelativePath(projectDirectory, csFilePath).Replace('\\', '/'),
        };

        try
        {
            Serializer.Save(csFilePath + ".meta", meta);
            return meta.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the meta file for {CsFilePath}", csFilePath);
            return Guid.Empty;
        }
    }

    static Guid ReadAssetId(string metaPath, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (doc.RootElement.TryGetProperty("Id", out var idEl) &&
                Guid.TryParse(idEl.GetString(), out var id))
            {
                return id;
            }

            logger.LogWarning("No valid 'Id' field in meta file {MetaPath}", metaPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read AssetId from {MetaPath}", metaPath);
        }

        return Guid.Empty;
    }

    static string? ExtractPrimaryClassFqn(string csFilePath, ILogger logger)
    {
        try
        {
            var source = File.ReadAllText(csFilePath);
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            var allClasses = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            if (allClasses.Count == 0)
                return null;

            // Prefer the class whose name matches the file name (one-class-per-file convention).
            var expectedName = Path.GetFileNameWithoutExtension(csFilePath);
            var cls = allClasses.FirstOrDefault(c => c.Identifier.Text == expectedName)
                      ?? allClasses[0];

            var ns = GetNamespace(root);
            return ns is not null ? $"{ns}.{cls.Identifier.Text}" : cls.Identifier.Text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse class declarations from {CsFilePath}", csFilePath);
            return null;
        }
    }

    static string? GetNamespace(SyntaxNode root)
    {
        var fileScopedNs = root.DescendantNodes()
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScopedNs is not null)
            return fileScopedNs.Name.ToString();

        return root.DescendantNodes()
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault()
            ?.Name.ToString();
    }
}
