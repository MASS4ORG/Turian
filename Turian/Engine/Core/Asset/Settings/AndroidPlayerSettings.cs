namespace Turian.Engine.Core;

/// <summary>The kind of package an Android export produces.</summary>
public enum AndroidPackageFormat
{
    /// <summary>An installable APK.</summary>
    Apk,

    /// <summary>An Android App Bundle for store upload, together with an installable APK.</summary>
    Aab,
}

/// <summary>The Android install identity, signing and back-button policy for a client export.</summary>
[CreateAssetMenu(fileName: "AndroidPlayerSettings", path: "Settings/Android Player Settings")]
[TypeId("97456191-f10a-4d65-9fb6-81f230a815a3")]
public class AndroidPlayerSettings : ProjectSettingsAsset
{
    /// <summary>The positive version code Android uses to order application updates.</summary>
    public int VersionCode { get; set; } = 1;

    /// <summary>Whether system Back quits the session; false delivers an Escape input event, as on a keyboard.</summary>
    public bool BackButtonQuits { get; set; }

    /// <summary>The package a non-development export produces.</summary>
    public AndroidPackageFormat PackageFormat { get; set; } = AndroidPackageFormat.Apk;

    /// <summary>The release keystore, absolute or relative to the project; its passwords come from the environment.</summary>
    public string? KeystorePath { get; set; }

    /// <summary>The alias of the signing key inside <see cref="KeystorePath"/>.</summary>
    public string? KeyAlias { get; set; }
}
