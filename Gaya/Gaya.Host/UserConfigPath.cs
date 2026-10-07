namespace Gaya.Host;

/// <summary>
/// Where the host keeps its per-user files — the dock layout, the user-scoped settings. They live in
/// <c>~/.gaya</c>, beside the tool rather than inside a platform config tree, so the same path
/// holds on every system and a user can find and edit them.
/// </summary>
static class UserConfigPath
{
    /// <summary>The per-user directory, created lazily by whatever writes into it.</summary>
    public static string Directory => GayaConfig.Directory;

    /// <summary>The absolute path of a per-user file.</summary>
    /// <param name="fileName">The file's name, e.g. <c>layout.json</c>.</param>
    /// <returns>An absolute path; the directory is not created.</returns>
    public static string For(string fileName) => Path.Combine(Directory, fileName);
}
