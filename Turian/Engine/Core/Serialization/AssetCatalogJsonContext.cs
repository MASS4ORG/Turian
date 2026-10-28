namespace Turian.Engine.Core;

/// <summary>Build-time JSON metadata for the catalog read before game assemblies or assets are activated.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(AssetCatalog))]
sealed partial class AssetCatalogJsonContext : JsonSerializerContext;
