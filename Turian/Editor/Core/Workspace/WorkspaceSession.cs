namespace Turian.Editor.Core;

/// <summary>
/// Which assets were open and which was active, so reopening a project restores the desk rather than
/// guessing what to show.
/// </summary>
/// <param name="OpenAssetIds">The open assets, in tab order.</param>
/// <param name="ActiveAssetId">The asset that was in front, if any.</param>
public sealed record WorkspaceSession(IReadOnlyList<Guid> OpenAssetIds, Guid? ActiveAssetId)
{
    /// <summary>An empty session — nothing open.</summary>
    public static WorkspaceSession Empty { get; } = new([], null);
}
