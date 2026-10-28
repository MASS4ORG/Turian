namespace Turian.Editor.Core;

/// <summary>Studio-only identity and appearance for one authored payload type.</summary>
/// <param name="PayloadType">The concrete asset type or an open generic base family.</param>
/// <param name="Id">Stable identifier used by browser type filters.</param>
/// <param name="DisplayName">Designer-facing name in menus and search filters.</param>
/// <param name="Icon">The icon theme identifier contributed by the editor brick.</param>
/// <param name="Color">Default icon tint, independent of engine runtime data.</param>
/// <param name="Category">Designer-facing grouping such as DDD/Variable.</param>
public sealed record AssetTypePresentation(Type PayloadType, string Id, string DisplayName,
    string Icon, Color32 Color, string Category)
{
    /// <summary>A readable glyph fallback when the active icon theme does not define this identifier.</summary>
    public string? Glyph { get; init; }

    /// <summary>Encoded default raster icon supplied by the owning Studio brick.</summary>
    public byte[]? ImageData { get; init; }
}

/// <summary>Contributes typed asset appearance without referencing concrete assets from Studio itself.</summary>
public interface IAssetPresentationProvider
{
    /// <summary>The type presentations contributed by this editor brick.</summary>
    IReadOnlyList<AssetTypePresentation> Presentations { get; }
}
