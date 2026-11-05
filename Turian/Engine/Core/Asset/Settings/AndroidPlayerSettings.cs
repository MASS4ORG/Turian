namespace Turian.Engine.Core;

/// <summary>The Android install identity and back-button policy for a client export.</summary>
[CreateAssetMenu(fileName: "AndroidPlayerSettings", path: "Settings/Android Player Settings")]
[TypeId("97456191-f10a-4d65-9fb6-81f230a815a3")]
public class AndroidPlayerSettings : ProjectSettingsAsset
{
    /// <summary>The positive version code Android uses to order application updates.</summary>
    public int VersionCode { get; set; } = 1;

    /// <summary>Whether system Back quits the session; false delivers an Escape input event.</summary>
    public bool BackButtonQuits { get; set; } = true;
}
