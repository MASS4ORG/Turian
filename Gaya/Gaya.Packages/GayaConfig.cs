namespace Gaya.Packages;

/// <summary>
/// Where Gaya keeps its per-user files: <c>~/.gaya</c>, or <c>GAYA_CONFIG_HOME</c> when set, so the same path holds
/// on every system and tests can redirect it.
/// </summary>
public static class GayaConfig
{
    /// <summary>The variable that redirects the per-user folder.</summary>
    public const string HomeVariable = "GAYA_CONFIG_HOME";

    /// <summary>The per-user folder, created lazily by whatever writes into it.</summary>
    public static string Directory
    {
        get
        {
            if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } directory)
                return Path.GetFullPath(directory);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return Path.Combine(string.IsNullOrEmpty(home) ? Path.GetTempPath() : home, ".gaya");
        }
    }

    /// <summary>The folder holding the studio's brick manifest, lock file and embedded studio bricks.</summary>
    public static string StudioRoot => Path.Combine(Directory, "studio");
}
