namespace Turian.Editor.Core;

/// <summary>
/// Where the Android tools are on this computer, and whether exports go straight to a device. These belong to the
/// person and the machine, never to a project: Studio shows them in its settings, and the CLI reads the same file.
/// </summary>
[Gaya.EditorSetting("Build/Android", Id = "turian.android",
    Description = "Where the Android tools are on this computer, and whether Android exports go straight to a device.")]
public sealed class AndroidToolchainSettings
{
    /// <summary>The Android SDK folder; empty finds it.</summary>
    [Gaya.EditorSetting("Android SDK", Description = "Leave empty to find it: ANDROID_HOME, ANDROID_SDK_ROOT, then the "
                                                + "folders Android Studio and the command-line tools install to.")]
    public string SdkDirectory { get; set; } = "";

    /// <summary>The JDK folder; empty finds it.</summary>
    [Gaya.EditorSetting("JDK", Description = "Leave empty to find it: JAVA_HOME, the JDK bundled with Android Studio, then "
                                        + "installed JDKs, version 17 or newer.")]
    public string JdkDirectory { get; set; } = "";

    /// <summary>Whether every Android export is installed on a device and started.</summary>
    [Gaya.EditorSetting("Install on Device", Description = "After an Android export, install it on the device and start it. "
                                                      + "Build & Run always does.")]
    public bool InstallOnDevice { get; set; }

    /// <summary>The device to install on; empty uses the only one connected.</summary>
    [Gaya.EditorSetting("Device", Description = "The serial adb lists the device by. Leave empty to use the only one "
                                           + "connected.")]
    public string Device { get; set; } = "";
}
