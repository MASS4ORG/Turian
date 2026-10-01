namespace Turian.Editor.Core;

/// <summary>Brings an open project in line with its bricks after they changed.</summary>
public interface IBrickApplier
{
    /// <summary>Loads new bricks' prebuilt assemblies, rescans assets and recompiles scripts.</summary>
    /// <exception cref="Gaya.Packages.PackageException">The bricks cannot be resolved.</exception>
    void ApplyBrickChanges();
}
