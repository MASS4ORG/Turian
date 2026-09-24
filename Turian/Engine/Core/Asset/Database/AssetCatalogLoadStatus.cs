namespace Turian.Engine.Core;

/// <summary>
/// The outcome of reading a project's <c>assetCatalog.json</c>.
/// </summary>
/// <remarks>
/// <see cref="Missing"/> and <see cref="Unreadable"/> both leave the database empty but mean
/// opposite things: the first is a project that has not been imported yet, the second is a damaged
/// cache. Collapsing them is what lets a truncated catalog present as a project that opens
/// successfully with every scene empty.
/// </remarks>
public enum AssetCatalogLoadStatus
{
    /// <summary>The catalog was read.</summary>
    Loaded,

    /// <summary>No catalog file exists yet; the project has not been imported.</summary>
    Missing,

    /// <summary>A catalog file exists but could not be parsed, so its records are lost.</summary>
    Unreadable
}
