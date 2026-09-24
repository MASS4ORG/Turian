namespace Turian.Engine.Core;

/// <summary>Which action maps the game's input is read through.</summary>
[CreateAssetMenu(fileName: "InputSettings", path: "Settings/Input Settings")]
[TypeId("a3000005-0000-4000-8000-000000000002")]
public class InputSettings : ProjectSettingsAsset
{
    /// <summary>
    /// The action maps loaded into <see cref="InputActionService"/> at startup. With none set, actions
    /// resolve to nothing and gameplay falls back to the raw <see cref="Input"/> facade.
    /// </summary>
    public AssetReference<DataAssetAsset>? Actions { get; set; }
}
