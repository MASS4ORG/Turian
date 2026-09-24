namespace Turian.Editor.Core;

/// <summary>
/// Tracks the state of an asset drag operation in the inspector.
/// </summary>
public static class AssetDragState
{
    /// <summary>Gets the currently dragged asset ID.</summary>
    public static Guid CurrentAssetId { get; private set; }

    /// <summary>Gets a value indicating whether a drag operation is active.</summary>
    public static bool IsActive => CurrentAssetId != Guid.Empty;

    /// <summary>Begins a new drag operation with the specified asset.</summary>
    public static void Begin(Guid assetId) => CurrentAssetId = assetId;

    /// <summary>Ends the current drag operation.</summary>
    public static void End() => CurrentAssetId = Guid.Empty;
}
