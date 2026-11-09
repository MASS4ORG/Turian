using Microsoft.Build.Construction;
using Turian.Engine.Core;

namespace Turian.Editor.Core;

/// <summary>Release signing and package format selection for Android exports.</summary>
public sealed partial class AndroidBuildPlatform
{
    /// <summary>The environment variable holding the release keystore password.</summary>
    public const string KeystorePasswordVariable = "TURIAN_ANDROID_KEYSTORE_PASSWORD";

    /// <summary>The environment variable holding the key password; the keystore password applies when unset.</summary>
    public const string KeyPasswordVariable = "TURIAN_ANDROID_KEY_PASSWORD";

    const string DebugKeyAlias = "androiddebugkey";

    static void ConfigureSigning(ProjectPropertyGroupElement properties, BuildPlatformContext context)
    {
        var android = context.Settings.Get<AndroidPlayerSettings>();
        var development = context.Profile?.Development == true;
        properties.AddProperty("AndroidPackageFormats",
            !development && android.PackageFormat == AndroidPackageFormat.Aab ? "aab;apk" : "apk");
        if (development || string.IsNullOrWhiteSpace(android.KeystorePath))
        {
            properties.AddProperty("AndroidKeyStore", "false");
            return;
        }

        properties.AddProperty("AndroidKeyStore", "true");
        properties.AddProperty("AndroidSigningKeyStore", KeystoreFile(context, android));
        properties.AddProperty("AndroidSigningKeyAlias", android.KeyAlias ?? string.Empty);
        properties.AddProperty("AndroidSigningStorePass", "env:" + KeystorePasswordVariable);
        var keyPassword = Environment.GetEnvironmentVariable(KeyPasswordVariable) is { Length: > 0 }
            ? KeyPasswordVariable : KeystorePasswordVariable;
        properties.AddProperty("AndroidSigningKeyPass", "env:" + keyPassword);
    }

    /// <summary>Explains why a release export cannot be signed, or returns null when it can.</summary>
    /// <param name="context">The export being prepared.</param>
    /// <returns>The problem to report, or null.</returns>
    internal static string? ReleaseSigningProblem(BuildPlatformContext context)
    {
        if (context.Profile?.Development == true
            || !string.Equals(context.Configuration, "Release", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var android = context.Settings.Get<AndroidPlayerSettings>();
        if (string.IsNullOrWhiteSpace(android.KeystorePath) || string.IsNullOrWhiteSpace(android.KeyAlias))
        {
            return "A release Android export needs a keystore: set KeystorePath and KeyAlias in Android Player Settings, or mark the profile as Development.";
        }

        var keystore = KeystoreFile(context, android);
        if (string.Equals(android.KeyAlias, DebugKeyAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(keystore), "debug.keystore", StringComparison.OrdinalIgnoreCase))
        {
            return "A release Android export cannot use the debug keystore.";
        }

        if (!File.Exists(keystore))
        {
            return $"The Android keystore '{keystore}' does not exist.";
        }

        return string.IsNullOrEmpty(Environment.GetEnvironmentVariable(KeystorePasswordVariable))
            ? $"Set the {KeystorePasswordVariable} environment variable to sign this release Android export."
            : null;
    }

    static string KeystoreFile(BuildPlatformContext context, AndroidPlayerSettings android) =>
        Path.GetFullPath(android.KeystorePath!, context.Settings.ProjectAbsoluteDir);
}
