namespace Turian.Engine.Core;

/// <summary>
/// How a texture source file is baked into cache artifacts. Settings are shared by default and
/// overridable per build target through <see cref="PerTarget"/>.
/// </summary>
public class TextureImportSettings
{
    /// <summary>
    /// Longest edge to upload, or <c>0</c> for the source resolution. Applied by dropping leading
    /// mip levels, which costs nothing and loses nothing for block-compressed data whose chain is
    /// already baked.
    /// </summary>
    public int MaxResolution { get; set; }

    /// <summary>
    /// Per-target overrides, keyed by <see cref="TextureBuildTarget"/>. A target absent from the
    /// map, or one whose entry leaves a value at its default, inherits the shared setting.
    /// </summary>
    public Dictionary<string, TextureTargetSettings> PerTarget { get; set; } = [];

    /// <summary>
    /// Resolves the effective resolution cap for <paramref name="target"/>.
    /// </summary>
    /// <param name="target">A key from <see cref="TextureBuildTarget"/>.</param>
    /// <returns>The longest edge to upload, or <c>0</c> for the source resolution.</returns>
    public int ResolveMaxResolution(string target) =>
        PerTarget.TryGetValue(target, out var overrides) && overrides.MaxResolution > 0
            ? overrides.MaxResolution
            : MaxResolution;
}
