namespace Turian.Editor.Core;

/// <summary>Contributes Studio asset creation entries from an editor brick.</summary>
public interface IAssetCreationProvider
{
    /// <summary>The designer-facing menu entries contributed by the brick.</summary>
    IReadOnlyList<AssetCreationKind> Kinds { get; }
}
