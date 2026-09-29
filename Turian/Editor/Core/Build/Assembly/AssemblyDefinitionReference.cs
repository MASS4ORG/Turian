namespace Turian.Editor.Core;

/// <summary>
/// Adds the scripts in its folder and subfolders to another <see cref="AssemblyDefinition"/>'s assembly, the Unity
/// asmref counterpart: how a project extends a package's assembly, or spreads one assembly over several folders.
/// </summary>
[CreateAssetMenu(fileName: "NewAssemblyReference", path: "Scripting/Assembly Definition Reference")]
[TypeId(TypeIdValue)]
public class AssemblyDefinitionReference : DataAsset
{
    /// <summary>The <see cref="TypeIdAttribute"/> value, read by build tools without loading the asset.</summary>
    public const string TypeIdValue = "a3000002-0000-4000-8000-000000000007";

    /// <summary>The assembly definition whose assembly the folder's scripts compile into.</summary>
    public AssetReference<DataAssetAsset>? Definition { get; set; }
}
