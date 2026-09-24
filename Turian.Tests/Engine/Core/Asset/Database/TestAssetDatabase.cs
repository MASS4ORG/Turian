namespace Turian.Tests;

/// <summary>
/// Clears the <see cref="AssetDatabase"/> singleton between tests, so each test can construct its
/// own instance without tripping the "already initialized" guard.
/// </summary>
static class TestAssetDatabase
{
    /// <summary>Drops the current singleton instance.</summary>
    public static void Reset() =>
        typeof(AssetDatabase)
            .GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
}
