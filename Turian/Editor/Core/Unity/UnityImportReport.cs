namespace Turian.Editor.Core;

/// <summary>What importing a Unity package did with one asset.</summary>
/// <param name="Source">The asset's path in the Unity project.</param>
/// <param name="Target">Where it went, relative to the destination; null when it was skipped.</param>
/// <param name="Note">Why it was skipped, or what did not carry over.</param>
public sealed record UnityImportEntry(string Source, string? Target, string? Note);

/// <summary>The outcome of importing a Unity package.</summary>
public sealed class UnityImportReport
{
    /// <summary>Assets that were imported, some with a note about what did not carry over.</summary>
    public List<UnityImportEntry> Converted { get; } = [];

    /// <summary>Assets that were left out, with the reason.</summary>
    public List<UnityImportEntry> Skipped { get; } = [];

    /// <summary>Things worth knowing that belong to no single asset.</summary>
    public List<string> Notes { get; } = [];
}
