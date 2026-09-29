namespace Turian.Editor.Core;

/// <summary>
/// Compiles the scripts in its folder and subfolders into an assembly of their own, the Unity asmdef
/// counterpart. A subfolder with its own definition belongs to that one; scripts outside every definition
/// compile into the project's default assembly, which references every definition that is not editor-only.
/// </summary>
[CreateAssetMenu(fileName: "NewAssembly", path: "Scripting/Assembly Definition")]
[TypeId(TypeIdValue)]
public class AssemblyDefinition : DataAsset
{
    /// <summary>The <see cref="TypeIdAttribute"/> value, read by build tools without loading the asset.</summary>
    public const string TypeIdValue = "a3000002-0000-4000-8000-000000000006";

    /// <summary>The assembly name; empty uses the file name.</summary>
    public string? Name { get; set; }

    /// <summary>The root namespace of the generated project; empty uses the assembly name.</summary>
    public string? RootNamespace { get; set; }

    /// <summary>The assembly definitions this one compiles against.</summary>
    public List<AssetReference<DataAssetAsset>> References { get; set; } = [];

    /// <summary>Compiled and loaded by the editor only, never shipped in a game.</summary>
    public bool EditorOnly { get; set; }

    /// <summary>Whether the default assembly references this one without being told to.</summary>
    public bool AutoReferenced { get; set; } = true;

    /// <summary>Allows <c>unsafe</c> code in the assembly.</summary>
    public bool AllowUnsafeCode { get; set; }
}
