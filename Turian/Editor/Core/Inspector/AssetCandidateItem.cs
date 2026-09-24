namespace Turian.Editor.Core;

/// <summary>
/// Represents a candidate asset in the asset reference picker.
/// </summary>
/// <param name="AssetId">The GUID of the asset.</param>
/// <param name="Name">The display name of the asset.</param>
/// <param name="AssetTypeName">The type name of the asset.</param>
public sealed record AssetCandidateItem(Guid AssetId, string Name, string AssetTypeName);
