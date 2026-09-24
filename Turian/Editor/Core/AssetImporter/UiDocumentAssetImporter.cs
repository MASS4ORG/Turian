namespace Turian.Editor.Core;

/// <summary>
/// Importer for <c>.ui</c> documents. Parses and validates the XML at import time — a malformed
/// document fails the import rather than the frame — bakes the normalised <see cref="UiDocument"/>
/// JSON as <c>primary.amui</c>, and binds every stylesheet, texture and font the document
/// references to that file's own asset id so the dependency graph, reimport hashing and
/// <c>.oap</c> packing all see them.
/// </summary>
public sealed class UiDocumentAssetImporter : IAssetImporter
{
    /// <summary>Baked-artifact extension: normalised <see cref="UiDocument"/> JSON.</summary>
    public const string ArtifactExtension = ".amui";

    // Attributes whose value is a project asset path the document depends on.
    static readonly string[] pathAttributes =
    [
        "src", "image", "image-normal", "image-hover", "image-pressed", "image-disabled",
        "icon", "background-image", "font",
    ];

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public bool IsValid(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath)
        && Path.GetExtension(filePath).Equals(".ui", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public Asset CreateAsset(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new UiDocumentAsset { RelativePath = filePath };
    }

    /// <summary>
    /// Parses the document and writes its normalised <see cref="UiDocument"/> JSON as the primary
    /// artifact. A malformed document is logged and baked as an empty document rather than thrown:
    /// the shared import pipeline has no per-asset isolation, so throwing here would abort the
    /// whole project scan.
    /// </summary>
    /// <param name="asset">The document asset metadata.</param>
    /// <param name="sourcePath">Absolute path of the <c>.ui</c> file.</param>
    /// <param name="importDirectory">Absolute path of the asset's import directory.</param>
    /// <returns>The single baked artifact name.</returns>
    public IReadOnlyList<string> ImportToCache(Asset asset, string sourcePath, string importDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(importDirectory);

        UiDocument document;
        try
        {
            document = UiXmlParser.Parse(File.ReadAllText(sourcePath), sourcePath);
        }
        catch (UiParseException ex)
        {
            Log.Logger.LogError(ex, "UI document {Path} is malformed; it will render nothing at runtime", sourcePath);
            document = new UiDocument();
        }

        var fileName = $"{IAssetImporter.PrimaryArtifactName}{ArtifactExtension}";
        File.WriteAllText(Path.Combine(importDirectory, fileName), document.ToJson());
        return [fileName];
    }

    /// <summary>
    /// Registers every file the document references — stylesheets, button/icon textures, fonts —
    /// as its own asset so it is imported once and stays swappable. Yields no child assets: the
    /// referenced files are real files with their own metadata, not payload embedded in the <c>.ui</c>.
    /// </summary>
    /// <param name="parentAssetId">The document's asset id.</param>
    /// <param name="filePath">Absolute path of the <c>.ui</c> file.</param>
    /// <param name="context">The pipeline, used to bind referenced files to their asset ids.</param>
    public IEnumerable<Asset> CreateChildAssets(Guid parentAssetId, string filePath, IAssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        UiDocument document;
        try
        {
            document = UiXmlParser.Parse(File.ReadAllText(filePath), filePath);
        }
        catch (Exception ex) when (ex is UiParseException or IOException)
        {
            yield break;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;

        foreach (var reference in CollectReferences(document))
        {
            if (!LooksLikePath(reference)) continue;
            var absolute = Path.GetFullPath(Path.Combine(baseDir, reference));
            if (File.Exists(absolute))
                context.EnsureAsset(absolute);
        }
    }

    static IEnumerable<string> CollectReferences(UiDocument document)
    {
        foreach (var src in document.StyleSheets)
            yield return src;

        foreach (var element in document.Elements())
            foreach (var attribute in pathAttributes)
                if (element.Attributes.TryGetValue(attribute, out var value) && !string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
    }

    static bool LooksLikePath(string value) =>
        !value.StartsWith('{')                                            // not a binding expression
        && !value.StartsWith("asset://", StringComparison.OrdinalIgnoreCase)
        && value.Contains('.', StringComparison.Ordinal);                 // has an extension
}
