namespace Turian.Editor.Core;

/// <summary>What opening an asset from the asset browser does.</summary>
public enum AssetActivation
{
    /// <summary>Nothing. A file the studio neither edits nor has a program to hand it to.</summary>
    None,

    /// <summary>The asset opens as a document in the studio.</summary>
    Edit,

    /// <summary>The asset is edited in the inspector, with no document of its own.</summary>
    Inspect,

    /// <summary>The source file goes to whatever program the desktop opens that kind with.</summary>
    ExternalProgram,
}

/// <summary>
/// One asset kind as the studio sees it: the source files it claims, what opening one does and how
/// the inspector titles its import settings. A plugin adding an asset type registers its own.
/// </summary>
/// <param name="Id">Stable unique id, e.g. <c>turian.texture</c>.</param>
/// <param name="DisplayName">Name shown wherever the kind is named, such as the inspector heading.</param>
/// <param name="Extensions">Source extensions it claims, leading dot included.</param>
/// <param name="Activation">What opening one of its files does.</param>
/// <param name="DefaultIcon">
/// The glyph the asset browser shows for a file of this kind when it has no live preview — either
/// because the kind never gets one (a script, a UI document) or because rendering one for every row
/// would be too costly (a material or a model, until thumbnail generation and caching land).
/// </param>
public sealed record AssetTypeDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    AssetActivation Activation,
    string DefaultIcon = "📄");

/// <summary>
/// Every asset kind the studio knows, keyed by source extension. It answers what a double click in
/// the asset browser should do, which is a decision about the asset type rather than about the shell,
/// so both the Guinevere studio and the CLI reach the same answer.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetTypeCatalog
{
    readonly Dictionary<string, AssetTypeDescriptor> byExtension =
        new(StringComparer.OrdinalIgnoreCase);

    readonly List<AssetTypeDescriptor> types = [];

    /// <summary>Creates the catalog with the built-in kinds registered.</summary>
    public AssetTypeCatalog()
    {
        foreach (var type in BuiltIn) Register(type);
    }

    /// <summary>The registered kinds, in registration order.</summary>
    public IReadOnlyList<AssetTypeDescriptor> Types => types;

    /// <summary>
    /// Adds a kind. A later registration wins for the extensions it claims, so a plugin can take over
    /// a built-in kind without the catalog having to know about it.
    /// </summary>
    /// <param name="type">The kind to add.</param>
    public void Register(AssetTypeDescriptor type)
    {
        ArgumentNullException.ThrowIfNull(type);

        types.RemoveAll(existing => existing.Id == type.Id);
        types.Add(type);

        foreach (var extension in type.Extensions) byExtension[extension] = type;
    }

    /// <summary>The kind claiming a file's extension, or null when nothing does.</summary>
    /// <param name="path">A source file path; only its extension is read.</param>
    /// <returns>The kind, or null.</returns>
    public AssetTypeDescriptor? Resolve(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : byExtension.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>What opening a file does. An unclaimed extension does nothing.</summary>
    /// <param name="path">A source file path.</param>
    /// <returns>The activation to perform.</returns>
    public AssetActivation ActivationFor(string? path) => Resolve(path)?.Activation ?? AssetActivation.None;

    /// <summary>
    /// The kinds the studio ships with. Scenes open as documents; materials and data assets are edited
    /// in the inspector; source files the engine bakes but cannot author — images, models — and the
    /// text files that are never converted at all go to the program the desktop opens them with;
    /// anything else does nothing, which is what an unclaimed extension falls through to.
    /// </summary>
    static IEnumerable<AssetTypeDescriptor> BuiltIn =>
    [
        new("turian.scene", "Scene", [".prefab"], AssetActivation.Edit, "🎬"),
        new("turian.material", "Material", [".material"], AssetActivation.Inspect, "🎨"),
        new("turian.dataAsset", "Data Asset", [".dataasset", ".asset", ".data"], AssetActivation.Inspect, "📦"),
        new("turian.texture", "Texture", [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".webp", ".dds"],
            AssetActivation.ExternalProgram, "🖼"),
        new("turian.model", "Model", [".obj", ".fbx", ".gltf", ".glb", ".dae", ".blend"],
            AssetActivation.ExternalProgram, "🧊"),
        new("turian.script", "Script", [".cs"], AssetActivation.ExternalProgram, "📜"),
        new("turian.text", "Text", [".txt", ".md", ".json", ".xml", ".csv", ".ini", ".strings"],
            AssetActivation.ExternalProgram),
        new("turian.uiDocument", "UI Document", [".ui"], AssetActivation.ExternalProgram, "🖥"),
        new("turian.uiStyleSheet", "UI Style Sheet", [".uss"], AssetActivation.ExternalProgram, "🧵"),
    ];
}
